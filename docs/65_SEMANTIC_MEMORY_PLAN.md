# Semantic Memory — Phase 1 설계 문서

## 개요

유저별 장기 기억(semantic memory)을 MongoDB에 저장하고, 세션 내 첫 메시지 시 해당 유저의 기억을 LLM 컨텍스트에 주입하는 기능.

Compaction과 완전히 분리된 독립 서비스로 구성한다.

---

## 아키텍처 결정 사항

| 항목 | 결정 | 근거 |
|------|------|------|
| Compaction 연동 방식 | 호출 측(CompactionBackgroundService, MessageHandler)에서 compaction 완료 후 별도 호출 | Compaction과 Semantic Memory는 관심사 분리 |
| 저장소 | MongoDB `semantic_memory` 컬렉션 | **Phase 2에서 Qdrant 마이그레이션 예정** |
| 추출 입력 | `context.Messages` 전체 (System + Persona 포함) | Gemini explicit cache hit 활용 |
| Fact 구조 | Flat `List<SemanticMemoryFact>` + `Type` 필드 | Qdrant 마이그레이션 시 fact 단위 vector 변환 용이 |
| 추출 타입 | `trait` (성향) / `event` (이벤트) / `episode` (에피소드) 3종 분리 | 목적별 검색 및 주입 제어를 위함 |
| 압축 threshold | Type별 별도 임계값 (`appsettings.json` 설정) | 타입별 누적 속도 차이 반영 |
| 주입 role | User role, `<semantic_memory>` 블록 | LLM이 유저 맥락으로 해석 |
| 주입 포맷 | DM 포함 모든 경우에 `[Username]` 태그 사용 | 포맷 일관성 |
| 압축 모델 | Phase 1: `ILLMProvider` 기본 모델 사용 | **Phase 2 TODO**: `ILLMProvider.GenerateAsync`에 optional model 파라미터 추가 후 `CompressionModel` 적용 |
| 중복 추출 방지 | `ConversationContext.SemanticMemoryRefs`(List\<ulong\>) — `Any()` 시 skip | 재시작/재시도 시 동일 session 중복 처리 방지 |
| 추출 실패 격리 | `ExtractAndSaveAsync` 예외 → 호출 측에서 LogError 후 무시 | Compaction은 이미 완료 상태 |

---

## 데이터 모델 Contract

### `SemanticMemoryFact` (record)

```csharp
namespace ArisuBot.Core.Models;

public record SemanticMemoryFact
{
    // 독립적으로 이해 가능한 사실 문장 (subject 포함, 완결형)
    public string Content { get; init; } = string.Empty;

    // 사실 분류: "trait" | "event" | "episode"
    // trait   — 지속적 성향, 선호, 습관
    // event   — 외부에서 발생한 구체적 사건
    // episode — 대화 내에서 나온 경험담 또는 이야기
    public string Type { get; init; } = string.Empty;

    // 이 fact를 추출한 원본 세션 ID
    public string SourceContextId { get; init; } = string.Empty;

    // 추출 시각 (UTC)
    public DateTime ExtractedAt { get; init; } = DateTime.UtcNow;
}
```

### `SemanticMemoryDocument` (MongoDB 도큐먼트)

```csharp
namespace ArisuBot.Infrastructure.MongoDB.Documents;

[BsonIgnoreExtraElements]
public class SemanticMemoryDocument
{
    // Discord User ID를 primary key로 사용 (_id)
    [BsonId]
    [BsonRepresentation(BsonType.Int64)]
    public ulong UserId { get; set; }

    // 해당 유저에 대해 누적된 사실 목록 (trait + event + episode 혼합)
    public List<SemanticMemoryFact> Facts { get; set; } = new();

    // 마지막 갱신 시각 (UTC)
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
```

**컬렉션명**: `semantic_memory`  
**인덱스**: `UserId` (`_id`이므로 자동 인덱싱)

### `ConversationContext` 추가 필드

```csharp
// 이 세션에서 semantic memory가 주입된 Discord User ID 목록.
// DB persist — 재시작 후에도 동일 세션 내 재주입 방지.
public HashSet<ulong> InjectedSemanticMemoryUserIds { get; set; } = new();

// 이 세션(old session)에서 semantic memory 추출이 완료된 Discord User ID 목록.
// Any() == true이면 이 세션에 대한 추출을 skip한다.
public List<ulong> SemanticMemoryRefs { get; set; } = new();
```

**`ConversationDocument` 매핑**: 두 필드 모두 persist. 기존 도큐먼트는 `BsonIgnoreExtraElements`로 null→빈 컬렉션 처리.

---

## 인터페이스 Contract

### `ISemanticMemoryRepository`

```csharp
namespace ArisuBot.Core.Interfaces;

public interface ISemanticMemoryRepository
{
    // userId에 해당하는 도큐먼트 반환. 없으면 null.
    Task<SemanticMemoryDocument?> GetByUserIdAsync(ulong userId, CancellationToken ct = default);

    // facts를 userId 도큐먼트에 append upsert.
    // 도큐먼트 없으면 신규 생성.
    Task AppendFactsAsync(ulong userId, IEnumerable<SemanticMemoryFact> facts, CancellationToken ct = default);

    // userId 도큐먼트의 facts 배열 전체를 교체 (압축 후 호출).
    Task ReplaceFactsAsync(ulong userId, IEnumerable<SemanticMemoryFact> facts, CancellationToken ct = default);
}
```

### `ISemanticMemoryService`

```csharp
namespace ArisuBot.Core.Interfaces;

public interface ISemanticMemoryService
{
    // compactedContext.Messages 전체를 LLM에 제공해 유저별 semantic memory 추출 및 저장.
    // compactedContext.SemanticMemoryRefs.Any() == true이면 즉시 반환 (중복 방지).
    // LLM 출력의 subject → compactedContext.Participants 딕셔너리로 userId 조회.
    //   조회 실패 시 LogError, 해당 유저 skip.
    // 저장 성공 userId → compactedContext.SemanticMemoryRefs append 후 context 저장.
    // 예외는 호출 측에 전파 (격리는 호출 측 책임).
    Task ExtractAndSaveAsync(ConversationContext compactedContext, CancellationToken ct = default);

    // userId의 semantic memory fact 목록 반환. 없으면 빈 리스트.
    Task<IReadOnlyList<SemanticMemoryFact>> GetFactsAsync(ulong userId, CancellationToken ct = default);
}
```

---

## LLM 추출 Contract

### 입력

`context.Messages` 전체 (System + Persona 포함).  
**Cache hint**: `context.DynamicCacheRef != null && context.CacheExpiresAt > DateTime.UtcNow` 이면 `CacheHint(context.DynamicCacheRef, context.CachedMessageCount)` 전달 → Gemini explicit cache hit 활용.

### 출력 스키마 (`responseSchema`)

```json
{
  "type": "object",
  "properties": {
    "users": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "subject":  { "type": "string" },
          "traits":   { "type": "array", "items": { "type": "string" } },
          "events":   { "type": "array", "items": { "type": "string" } },
          "episodes": { "type": "array", "items": { "type": "string" } }
        },
        "required": ["subject", "traits", "events", "episodes"]
      }
    }
  },
  "required": ["users"]
}
```

### 추출 프롬프트 위치

`src/ArisuBot.Host/prompts/semantic_memory_extraction.md`

### 타입 정의

| Type | 내용 |
|------|------|
| `trait` | 지속적 성향, 선호도, 습관 (예: "Xeno는 다크 모드를 선호한다") |
| `event` | 외부에서 발생한 구체적 사건 (예: "Xeno는 4월 20일 게임 대회에 참가했다") |
| `episode` | 대화에서 나온 경험담 또는 이야기 (예: "Xeno는 이전 직장에서 번아웃을 경험했다고 언급했다") |

---

## 압축 Contract

### 트리거

`ExtractAndSaveAsync` 내, userId별 `AppendFactsAsync` 후 type별 count 확인:

```
traitCount   = doc.Facts.Count(f => f.Type == "trait")
eventCount   = doc.Facts.Count(f => f.Type == "event")
episodeCount = doc.Facts.Count(f => f.Type == "episode")

traitCount   >= TraitCompressionThreshold   → trait 압축
eventCount   >= EventCompressionThreshold   → event 압축
episodeCount >= EpisodeCompressionThreshold → episode 압축
```

(count는 `AppendFacts` 후 in-memory로 계산 — DB 추가 조회 없음)

### 압축 실행

압축 트리거 type별로 **독립** LLM 호출:
1. 해당 type의 기존 facts content 목록 → LLM → 압축된 content 목록 반환
2. 전체 facts에서 해당 type 제거 + 압축 결과 추가 → `ReplaceFactsAsync`

### 압축 출력 스키마

```json
{
  "type": "array",
  "items": { "type": "string" }
}
```

### 압축 프롬프트 위치

`src/ArisuBot.Host/prompts/semantic_memory_compression.md`

---

## 설정 Contract

### `SemanticMemoryOptions`

```csharp
namespace ArisuBot.Core.Options;

public class SemanticMemoryOptions
{
    public const string SectionName = "SemanticMemory";

    // trait 압축 트리거 임계값 (userId당 trait count)
    public int TraitCompressionThreshold { get; set; } = 20;

    // event 압축 트리거 임계값 (userId당 event count)
    public int EventCompressionThreshold { get; set; } = 20;

    // episode 압축 트리거 임계값 (userId당 episode count)
    public int EpisodeCompressionThreshold { get; set; } = 20;

    // [Phase 2 예약] 압축 LLM 모델 지정. 현재 미사용.
    // ILLMProvider.GenerateAsync model 파라미터 추가 후 적용 예정.
    public string? CompressionModel { get; set; }
}
```

### `appsettings.json` 추가 섹션

```json
{
  "SemanticMemory": {
    "TraitCompressionThreshold": 20,
    "EventCompressionThreshold": 20,
    "EpisodeCompressionThreshold": 20,
    "CompressionModel": null
  }
}
```

---

## 주입 Contract

### 조건

- `ProcessBatchAsync` 내, `newMessage` 생성 후, LLM 호출 전
- batch 내 각 userId에 대해 `InjectedSemanticMemoryUserIds`에 없으면 주입 시도
- facts 없는 userId도 `InjectedSemanticMemoryUserIds`에 추가 (동일 세션 내 재조회 방지)

### DB 저장 vs LLM 전달 분리

```
newMessage      → DB 저장 (원본 content 유지)
llmUserMessage  → LLM 전달 (semantic memory block 포함 가능)
```

### 주입 포맷 — 채널 (복수 유저)

```
<semantic_memory>
[Xeno]
- [trait] Xeno는 다크 모드를 선호한다.
- [event] Xeno는 4월 20일 게임 세션에 참여했다.
[Alice]
- [episode] Alice는 이전 직장에서 번아웃을 경험했다고 언급했다.
</semantic_memory>

[Xeno]: 안녕
[Alice]: 반가워
```

### 주입 포맷 — DM (단일 유저)

```
<semantic_memory>
[Xeno]
- [trait] Xeno는 다크 모드를 선호한다.
- [event] Xeno는 4월 20일 게임 세션에 참여했다.
</semantic_memory>

안녕
```

---

## 호출 측 Contract

### `CompactionBackgroundService`

```csharp
try { await _compactionService.RunAsync(context, ct); }
catch (Exception ex)
{
    _logger.LogError(ex, "[BackgroundCompaction] Compaction 실패 — contextId={Id}", context.Id);
    continue;
}

// Compaction 성공 후 독립 실행 — 실패해도 루프 계속
try { await _semanticMemoryService.ExtractAndSaveAsync(context, ct); }
catch (Exception ex)
{
    _logger.LogError(ex, "[BackgroundCompaction] SemanticMemory 추출 실패 — contextId={Id}", context.Id);
}
```

### `MessageHandler` (fire-and-forget)

```csharp
_ = Task.Run(async () =>
{
    try { await _compactionService.RunAsync(context, CancellationToken.None); }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Compaction 실행 실패 — contextId={Id}", context.Id);
        return; // Compaction 실패 시 semantic memory 추출 중단
    }

    try { await _semanticMemoryService.ExtractAndSaveAsync(context, CancellationToken.None); }
    catch (Exception ex)
    {
        _logger.LogError(ex, "SemanticMemory 추출 실패 — contextId={Id}", context.Id);
    }
});
```

---

## 테스트 범위

### Unit — `SemanticMemoryServiceTests`

| 케이스 | 검증 내용 |
|--------|----------|
| `SemanticMemoryRefs` not empty | skip (즉시 반환) |
| subject 매핑 성공 | 올바른 userId로 fact 저장, Type 분류 정확 |
| subject 매핑 실패 | LogError 호출, 해당 유저 skip, 나머지 계속 |
| trait threshold 미달 | trait 압축 LLM 미호출 |
| trait threshold 도달 | trait LLM 호출, trait facts만 교체 |
| event / episode 각 threshold 독립 | 다른 type 압축에 영향 없음 |
| 캐시 유효 | CacheHint 전달 확인 |
| 캐시 만료 / null | CacheHint null 전달 확인 |
| 부분 성공 | 성공 userId만 SemanticMemoryRefs에 추가 |

### Unit — `SemanticMemoryInjectionTests`

| 케이스 | 검증 내용 |
|--------|----------|
| 미주입 userId, facts 있음 | `<semantic_memory>` 블록 prepend, `[Username]` 태그 포함 |
| 이미 주입 userId | content 변경 없음 |
| facts 없음 | content 변경 없음, userId는 InjectedSet에 추가 |
| 복수 userId batch | 미주입 전원 처리, 복수 섹션 포함 |
| DM 단일 유저 | `[Username]` 태그 포함 |
| DB 저장용 message | 원본 content 유지 (semantic_memory 블록 없음) |

### Integration — `SemanticMemoryRepositoryTests`

| 케이스 | 검증 내용 |
|--------|----------|
| AppendFacts (신규) | 도큐먼트 생성 확인 |
| AppendFacts (기존) | facts 누적, type 혼합 확인 |
| ReplaceFactsAsync | facts 배열 전체 교체 확인 |
| GetByUserIdAsync | 올바른 도큐먼트 반환 |

---

## Phase 2 TODO

- [ ] Qdrant 마이그레이션 — `semantic_memory` MongoDB → Qdrant vector store. `SemanticMemoryFact` 단위 vector 변환.
- [ ] `ILLMProvider.GenerateAsync`에 optional `modelOverride` 파라미터 추가
- [ ] `SemanticMemoryOptions.CompressionModel` 실제 적용
- [ ] LLM tool — 유저가 semantic memory 직접 조회 가능하도록
