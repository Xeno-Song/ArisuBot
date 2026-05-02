# Semantic Memory — Phase 1 설계 문서

## 개요

유저별 장기 기억(semantic memory)을 MongoDB에 session 단위로 저장하고, 이후 세션의 첫 메시지 시 해당 유저의 최신 기억을 LLM 컨텍스트에 주입하는 기능.

Compaction과 완전히 분리된 독립 서비스로 구성한다.

---

## 아키텍처 결정 사항

| 항목 | 결정 | 근거 |
|------|------|------|
| Compaction 연동 방식 | 호출 측(CompactionBackgroundService, MessageHandler)에서 compaction 완료 후 별도 호출 | Compaction과 Semantic Memory는 관심사 분리 |
| 저장소 | MongoDB `semantic_memory` 컬렉션 | **Phase 2에서 Qdrant 마이그레이션 예정** |
| 저장 단위 | **session별 document 신규 생성** (per-user 갱신 아님) | 히스토리 보존, rollback 가능 |
| 추출 입력 | `context.Messages` 전체 (System + Persona 포함) | Gemini explicit cache hit 활용 |
| 데이터 구조 | `extracted` (이번 session) + `snapshot` (누적) dual 필드 | rollback 기준점 보존, LLM 주입 효율 |
| 추출 카테고리 | `traits` (성향/선호/습관) + `episodic` (경험/사건/에피소드) | events/episodes를 episodic으로 통합 |
| state 관리 | `"active"` / `"inactive"` 수동 관리 | inactive 설정 시 해당 document 주입 제외 → rollback 수단 |
| 압축 threshold | snapshot의 category별 count 기준 | 이전 누적분 포함 전체 count |
| 압축 실패 안전 | 빈 결과 반환 시 원본 보존 | 데이터 손실 방지 |
| 주입 기준 | 최신 active document의 snapshot | 가장 최근 정보 사용 |
| 주입 role | User role, `<semantic_memory>` 블록 | LLM이 유저 맥락으로 해석 |
| 압축 모델 | Phase 1: `ILLMProvider` 기본 모델 사용 | **Phase 2 TODO**: `ILLMProvider.GenerateAsync`에 optional model 파라미터 추가 후 `CompressionModel` 적용 |
| 중복 추출 방지 | `ConversationContext.SemanticMemoryRefs`(List\<ulong\>) — `Any()` 시 skip | 재시작/재시도 시 동일 session 중복 처리 방지 |

---

## 데이터 모델 Contract

### `SemanticMemoryData`

```csharp
namespace ArisuBot.Core.Models;

/// <summary>Semantic memory 데이터 단위. extracted / snapshot 양쪽에 사용.</summary>
public class SemanticMemoryData
{
    public List<string> Traits   { get; set; } = new(); // 성향·선호·습관
    public List<string> Episodic { get; set; } = new(); // 경험·사건·에피소드
}

public static class SemanticMemoryState
{
    public const string Active   = "active";
    public const string Inactive = "inactive";
}
```

### `UserSemanticMemory` (도메인 모델)

```csharp
namespace ArisuBot.Core.Models;

public class UserSemanticMemory
{
    public string Id        { get; set; } = string.Empty; // MongoDB ObjectId (InsertOne 후 채워짐)
    public ulong  UserId    { get; set; }                  // Discord User ID
    public string SessionId { get; set; } = string.Empty; // 추출 원본 session ID
    public string State     { get; set; } = SemanticMemoryState.Active;
    public SemanticMemoryData Extracted { get; set; } = new(); // 이번 session 추출분
    public SemanticMemoryData Snapshot  { get; set; } = new(); // 이전 snapshot + Extracted 누적
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

### `SemanticMemoryDocument` (MongoDB 도큐먼트)

```
컬렉션: semantic_memory
```

| 필드 | BSON key | 설명 |
|------|----------|------|
| `Id` | `_id` (ObjectId) | MongoDB 자동 생성 |
| `UserId` | `userId` | Discord User ID (문자열 저장) |
| `SessionId` | `sessionId` | 추출 원본 session ID |
| `State` | `state` | "active" / "inactive" |
| `Extracted` | `extracted` | `{ traits: [], episodic: [] }` |
| `Snapshot` | `snapshot` | `{ traits: [], episodic: [] }` |
| `CreatedAt` | `createdAt` | GetLatestActiveAsync 정렬 기준 |

**인덱스**: `(userId, state, createdAt)` — `GetLatestActiveAsync` 조회 최적화

### `ConversationContext` 추가 필드

```csharp
// 이 세션에서 semantic memory가 주입된 Discord User ID 목록.
// DB persist — 재시작 후에도 동일 세션 내 재주입 방지.
public HashSet<ulong> InjectedSemanticMemoryUserIds { get; set; } = new();

// 이 세션에서 semantic memory 추출이 완료된 Discord User ID 목록.
// Any() == true이면 이 세션에 대한 추출을 skip한다.
public List<ulong> SemanticMemoryRefs { get; set; } = new();
```

---

## 인터페이스 Contract

### `ISemanticMemoryRepository`

```csharp
namespace ArisuBot.Core.Interfaces;

public interface ISemanticMemoryRepository
{
    // userId의 가장 최신 active document 반환. 없으면 null.
    // createdAt 내림차순 정렬 후 첫 번째 active document.
    Task<UserSemanticMemory?> GetLatestActiveAsync(ulong userId, CancellationToken ct = default);

    // 신규 session memory document 삽입. Id는 MongoDB가 생성.
    Task CreateAsync(UserSemanticMemory memory, CancellationToken ct = default);

    // 지정 document의 state를 변경 (수동 rollback/비활성화 용도).
    Task SetStateAsync(string id, string state, CancellationToken ct = default);
}
```

### `ISemanticMemoryService`

```csharp
namespace ArisuBot.Core.Interfaces;

public interface ISemanticMemoryService
{
    // context.Messages 전체를 LLM에 제공해 유저별 semantic memory 추출 및 저장.
    // context.SemanticMemoryRefs.Any() == true이면 즉시 반환 (중복 방지).
    // LLM 출력의 subject → context.Participants 딕셔너리로 userId 조회.
    //   조회 실패 시 LogError, 해당 유저 skip.
    // per-session document 신규 생성. snapshot = 이전 snapshot + 이번 extracted.
    // 저장 성공 userId → context.SemanticMemoryRefs append 후 context 저장.
    // 예외는 호출 측에 전파 (격리는 호출 측 책임).
    Task ExtractAndSaveAsync(ConversationContext context, CancellationToken ct = default);

    // userId의 가장 최신 active document의 snapshot 반환. 없으면 null.
    Task<SemanticMemoryData?> GetLatestSnapshotAsync(ulong userId, CancellationToken ct = default);
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
          "episodic": { "type": "array", "items": { "type": "string" } }
        },
        "required": ["subject", "traits", "episodic"]
      }
    }
  },
  "required": ["users"]
}
```

### 추출 프롬프트 위치

`src/ArisuBot.Host/prompts/semantic_memory_extraction.md`

### 카테고리 정의

| 카테고리 | 내용 |
|----------|------|
| `traits` | 지속적 성향, 선호도, 습관 (예: "Xeno는 다크 모드를 선호한다") |
| `episodic` | 구체적 사건, 경험담, 이야기 (예: "Xeno는 이전 직장에서 번아웃을 경험했다고 언급했다") |

---

## 압축 Contract

### 트리거

`ExtractAndSaveAsync` 내, snapshot 누적 후 count 확인:

```
snapshot.Traits.Count   >= TraitCompressionThreshold   → traits 압축
snapshot.Episodic.Count >= EpisodicCompressionThreshold → episodic 압축
```

### 압축 실행

각 카테고리 독립 LLM 호출:
1. 해당 카테고리의 snapshot 목록 → LLM → 압축된 목록 반환
2. 빈 결과 또는 파싱 실패 시 **원본 목록 보존** (데이터 손실 방지)
3. 압축 결과로 snapshot.Traits / snapshot.Episodic 교체 후 `CreateAsync`

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

    // snapshot의 trait 항목 수가 이 값 이상이면 압축 실행
    public int TraitCompressionThreshold { get; set; } = 20;

    // snapshot의 episodic 항목 수가 이 값 이상이면 압축 실행
    public int EpisodicCompressionThreshold { get; set; } = 20;

    // [Phase 2 예약] 압축 LLM 모델 지정. 현재 미사용.
    public string? CompressionModel { get; set; }
}
```

### `appsettings.json` 추가 섹션

```json
{
  "SemanticMemory": {
    "TraitCompressionThreshold": 20,
    "EpisodicCompressionThreshold": 20,
    "CompressionModel": null
  }
}
```

---

## 주입 Contract

### 조건

- `ProcessBatchAsync` 내, `newMessage` 생성 후, LLM 호출 전
- batch 내 각 userId에 대해 `InjectedSemanticMemoryUserIds`에 없으면 주입 시도
- snapshot null(기억 없음) 유저도 `InjectedSemanticMemoryUserIds`에 추가 (동일 세션 내 재조회 방지)

### DB 저장 vs LLM 전달 분리

```
newMessage      → DB 저장 (원본 content 유지)
llmUserMessage  → LLM 전달 (semantic memory block 포함 가능)
```

### 주입 포맷

```
<semantic_memory>
[Alice]
[Traits]
- Alice는 고양이를 좋아한다.
- Alice는 다크 모드를 선호한다.
[Episodic]
- Alice는 도쿄를 방문했다.

[Bob]
[Traits]
- Bob은 피자를 좋아한다.
</semantic_memory>

[Alice]: 안녕
[Bob]: 반가워
```

- `[Traits]` / `[Episodic]` 섹션은 해당 목록이 있을 때만 포함
- 모든 유저의 snapshot이 null / 빈 경우 블록 전체 생략

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

### Unit — `SemanticMemoryServiceTests` (27개)

| 케이스 | 검증 내용 |
|--------|----------|
| `SemanticMemoryRefs` not empty | skip (즉시 반환) |
| 이전 snapshot 없음 | extracted = snapshot 그대로 |
| 이전 snapshot 있음 | snapshot = 이전 + 신규 누적 |
| 성공 시 refs 추가 + context 저장 | `SemanticMemoryRefs.Add`, `SaveContextAsync` 1회 |
| subject 매핑 실패 | skip, 미저장 |
| zero users extracted | context 저장 후 return |
| empty extracted | document 생성 (빈 데이터) |
| trait threshold 미달 | LLM 1회 (extraction만) |
| trait threshold 도달 | snapshot.Traits 압축 결과로 교체 |
| episodic threshold 도달 | snapshot.Episodic 압축 결과로 교체 |
| 압축 빈 결과 → 원본 보존 | Traits.Count 3 유지 |
| 압축 invalid JSON → 원본 보존 | Traits.Count 3 유지 |
| 압축 fenced JSON | 정상 파싱 |
| 압축 malformed fence | fallback → 파싱 실패 → 원본 보존 |
| extraction fenced JSON | 정상 파싱 |
| extraction malformed fence | fallback → 파싱 실패 → context 저장 |
| extraction invalid JSON | context 저장 |
| `users` 속성 없음 | context 저장 |
| empty subject | skip |
| episodic 키 누락 | traits만 추출 |
| 유효 cache | CacheHint 전달 |
| 만료 cache | CacheHint null |
| null cacheRef | CacheHint null |
| cacheRef 있으나 expiresAt null | CacheHint null |
| GetLatestSnapshotAsync 없음 | null 반환 |
| GetLatestSnapshotAsync 있음 | snapshot 반환 |

### Unit — `SemanticMemoryInjectionTests` (10개)

| 케이스 | 검증 내용 |
|--------|----------|
| 단일 유저, traits | `[Name]`, `[Traits]`, `<semantic_memory>` 포함 |
| 단일 유저, episodic | `[Episodic]` 포함 |
| 복수 유저 | 모든 이름 태그 포함 |
| 복수 유저 순서 | Alice 섹션 < Bob 섹션 |
| 빈 목록 | null 반환 |
| null snapshot | null 반환 |
| 모든 유저 빈 snapshot | null 반환 |
| traits + episodic 혼합 | 두 섹션 모두 포함 |
| traits + episodic 순서 | Traits 섹션이 Episodic 앞 |
| traits만 있음 | Episodic 섹션 없음 |
| episodic만 있음 | Traits 섹션 없음 |

### Integration — `SemanticMemoryRepositoryTests` (11개)

| 케이스 | 검증 내용 |
|--------|----------|
| GetLatestActiveAsync — 없음 | null 반환 |
| GetLatestActiveAsync — 단일 active | 반환 |
| GetLatestActiveAsync — 복수 active | 최신 반환 |
| GetLatestActiveAsync — 모두 inactive | null 반환 |
| GetLatestActiveAsync — mixed states | 최신 active 반환 |
| CreateAsync — 신규 | 조회 성공 |
| CreateAsync — Id 채워짐 | memory.Id 비어있지 않음 |
| CreateAsync — state=active 기본 | active로 생성 |
| SetStateAsync — active→inactive | GetLatestActive에 미노출 |
| SetStateAsync — inactive→active | GetLatestActive에 노출 |
| SetStateAsync — 대상만 영향 | 다른 document 영향 없음 |
| 복수 userId 독립성 | 서로 간섭 없음 |

---

## Phase 2 TODO

- [ ] Qdrant 마이그레이션 — `semantic_memory` MongoDB → Qdrant vector store
- [ ] `ILLMProvider.GenerateAsync`에 optional `modelOverride` 파라미터 추가
- [ ] `SemanticMemoryOptions.CompressionModel` 실제 적용
- [ ] LLM tool — 유저가 semantic memory 직접 조회 가능하도록
- [ ] SetStateAsync 호출 UI/커맨드 — rollback 운영 도구
