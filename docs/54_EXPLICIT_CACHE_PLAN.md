# Gemini Explicit Cache 구현 계획

## 배경

Gemini implicit cache(자동 prefix 감지)가 실운영에서 안정적으로 hit되지 않음. 명시적 캐싱(Explicit Caching) API를 통해 systemInstruction + tools + 대화 이력을 캐시 자산으로 고정하여 장기 세션 비용을 최적화.

---

## 구조 개요

### 캐싱 전략: Rolling Dynamic Cache만 사용

Static Base Cache(systemInstruction + tools만)는 세션이 없는 시간이 더 길어 TTL 낭비가 크므로 채택하지 않음.

Dynamic Cache만 구현:
- 대상: systemInstruction + tools + 누적 대화 이력 전체
- 생성 조건: uncached 토큰이 임계값 이상 누적 **AND** 메시지 유입 속도 조건 충족
- 생성 시: 이전 cache 즉시 삭제 → 새 cache로 교체 (항상 활성 cache 최대 1개)

### Dynamic Cache 생명주기

```
[초기 대화]
  메시지 누적 → UncachedTokenCount 증가
  조건 미충족 → full context 전송 (캐시 없음)

[임계값 도달: UncachedTokenCount >= ThresholdTokens AND velocity 충족]
  Cache 생성: systemInst + tools + history[0..N]
  UncachedTokenCount = 0, CachedMessageCount = N
  이후 요청: config.CachedContent = cacheName, contents = messages[N..]

[대화 계속: uncached 누적]
  UncachedTokenCount += (tokensIn - tokensCachedIn) per turn

[임계값 재도달]
  새 Cache 생성: systemInst + tools + history[0..N+M]  (전체 이력 포함)
  이전 Cache 삭제
  UncachedTokenCount = 0, CachedMessageCount = N+M

[반복]
```

### 조건부 생성 (Velocity Gate)

단발성 대화에서 cache write 비용 낭비 방지:
- `VelocityWindowSeconds`(기본 180초) 내에 `VelocityMinMessages`(기본 3회) 이상 메시지 수신 시에만 cache 생성
- 기존 cache가 유효하면 velocity 미충족이어도 계속 **사용** (생성만 gate)

---

## Phase 1 — Explicit Cache 구현

### 신규 파일

| 파일 | 역할 |
|---|---|
| `src/ArisuBot.Core/Models/CacheHint.cs` | `record CacheHint(string CachedContentName, int CachedMessageCount)`. `GenerateAsync`에 cache 정보 전달용 |
| `src/ArisuBot.Core/Interfaces/ILLMCacheManager.cs` | cache lifecycle 추상화. `ArisuBot.Discord`가 `ArisuBot.LLM`을 직접 참조하지 않도록 Core에 위치 |
| `src/ArisuBot.LLM/Options/CacheOptions.cs` | `Enabled`, `ThresholdTokens`, `TtlSeconds`, `VelocityWindowSeconds`, `VelocityMinMessages` |
| `src/ArisuBot.LLM/Gemini/IGeminiCacheClient.cs` | Gemini cache CRUD 인터페이스. 테스트 mock용 |
| `src/ArisuBot.LLM/Gemini/GeminiCacheManager.cs` | `ILLMCacheManager` 구현. velocity + threshold 판단. cache 생성/교체/삭제. 생성 캐시 in-memory 추적(`ConcurrentDictionary`) |
| `src/ArisuBot.LLM/Gemini/GeminiCacheCleanupService.cs` | `IHostedService`. `StartAsync`: 고아 캐시 정리(`ListCachesAsync` → 전체 삭제). `StopAsync`: 추적 캐시 전체 삭제(정상 종료 대응) |
| `src/ArisuBot.Core/Interfaces/IEpisodicMemoryHandler.cs` | 빈 인터페이스. Episodic memory 핸들러 구조 예약 |
| `src/ArisuBot.Core/Interfaces/ISemanticMemoryHandler.cs` | 빈 인터페이스. Semantic memory 핸들러 구조 예약 |

### 수정 파일

| 파일 | 변경 내용 |
|---|---|
| `ConversationContext.cs` | `DynamicCacheRef?`, `CachedMessageCount`, `UncachedTokenCount`, `RecentMessageTimestamps` 필드 추가 |
| `ILLMProvider.cs` | `CacheHint? cacheHint = null` 파라미터 추가 |
| `ConversationDocument.cs` | 새 필드 BSON 매핑. 기존 문서 역직렬화 시 default값(0, null, 빈 리스트) 처리 |
| `IGeminiStreamClient.cs` | `CreateCacheAsync`, `DeleteCacheAsync`, `ListCachesAsync` 메서드 추가 |
| `GeminiStreamClient.cs` | 위 메서드 구현 (`_client.Caches.CreateAsync` 등) |
| `GeminiProvider.cs` | CacheHint 수신 → cache 모드 분기. CountTokens 정식화(try/catch 유지, warn 로그). 임시 dump 코드 제거 |
| `LLMServiceExtensions.cs` | `CacheOptions` 바인딩. `GeminiCacheManager`, `GeminiCacheCleanupService` DI 등록 |
| `ConversationService.cs` | `UpdateCacheStateAsync` 추가 (DynamicCacheRef, UncachedTokenCount, RecentMessageTimestamps 갱신 후 저장) |
| `MessageHandler.cs` | `ILLMCacheManager` 주입. 요청 전 `TryRollCacheAsync` 호출 → `CacheHint` 획득. 응답 후 `UncachedTokenCount` 갱신 |
| `appsettings.json` | `LLM:Cache` 섹션 추가 |

### 수정 파일 — 테스트

| 파일 | 변경 내용 |
|---|---|
| `GeminiCacheManagerTests.cs` (신규) | velocity 조건 판단, threshold 판단, cache 생성/교체/삭제 흐름 단위 테스트 |
| `GeminiProviderGenerateTests.cs` (수정) | CacheHint 전달 시 `config.CachedContent` 설정, `SystemInstruction`/`Tools` null 케이스 추가 |
| 기존 테스트 전체 | `GenerateAsync` 시그니처 변경 반영 (default null — 호출부 변경 없음, mock 시그니처만 수정) |

### 핵심 호출 흐름

```
MessageHandler.HandleAsync()
│
├─ GetContextAsync()
│    DynamicCacheRef?, CachedMessageCount, UncachedTokenCount, RecentMessageTimestamps 로드
│
├─ BuildMessageList()  →  full messages (system + history + new user)
│
├─ context.RecentMessageTimestamps에 현재 시각 추가 (window 초과분 prune)
│
├─ ILLMCacheManager.TryRollCacheAsync(context, messages, tools)
│   ┌─ Enabled = false                    → existing hint or null
│   ├─ velocity 미충족 (window 내 3회 미만) → existing hint or null
│   ├─ UncachedTokenCount < ThresholdTokens → existing hint or null
│   └─ threshold + velocity 충족
│       ├─ BuildSystemContent(messages)
│       ├─ BuildToolDeclarations(tools)
│       ├─ BuildHistoryContents(messages)  ← system 제외 전체 이력
│       ├─ IGeminiCacheClient.CreateAsync(model, systemInst, tools, contents, TTL)
│       ├─ if oldCacheRef: IGeminiCacheClient.DeleteAsync(oldCacheRef)
│       ├─ context.DynamicCacheRef = newName
│       ├─ context.CachedMessageCount = historyCount
│       ├─ context.UncachedTokenCount = 0
│       └─ return CacheHint(newName, historyCount)
│
├─ ILLMProvider.GenerateAsync(messages, tools, toolContext, cacheHint)
│   ┌─ cacheHint != null
│   │    contents = BuildContents(messages).Skip(cacheHint.CachedMessageCount)
│   │    config.CachedContent = cacheHint.CachedContentName
│   │    config.SystemInstruction = null   ← cache에 포함됨
│   │    config.Tools = null               ← cache에 포함됨
│   └─ cacheHint == null
│        contents = BuildContents(messages)  // 전체
│        config.SystemInstruction = BuildSystemInstruction(messages)
│        config.Tools = toolDeclarations
│
├─ CountTokensAsync() [정식 기능: 전송 전 토큰 수 진단 로그]
│
└─ 응답 후
     context.UncachedTokenCount += sum(tokensIn - tokensCachedIn)
     ConversationService.UpdateCacheStateAsync(context)
```

### TTL 만료된 캐시 처리

DB에 `DynamicCacheRef`가 있으나 Gemini 측에서 TTL 만료된 경우:
- GenerateAsync에서 API 오류 발생 → `MessageHandler`의 기존 catch에서 처리됨
- 다음 메시지 수신 시: `TryRollCacheAsync`가 만료 cache 삭제 시도(오류 무시) → 새 cache 생성 또는 no-cache로 진행
- 즉각 재시도 없음 (사양 결정)

### 설정 예시 (`appsettings.json`)

```json
{
  "LLM": {
    "Cache": {
      "Enabled": true,
      "InitialThresholdTokens": 5000,
      "RefreshThresholdTokens": 20000,
      "TtlSeconds": 1800,
      "VelocityWindowSeconds": 180,
      "VelocityMinMessages": 3
    }
  }
}
```

### 사이드 이펙트

1. **`ILLMProvider.GenerateAsync` 시그니처 변경**: `CacheHint?` 파라미터 추가. optional이므로 기존 호출부 변경 불필요. 테스트 mock 시그니처는 수정 필요.
2. **`ConversationContext` 필드 추가**: 기존 MongoDB 문서에 신규 필드 없음 → BSON 역직렬화 시 default값(0, null, `[]`) 적용. 하위 호환 유지.
3. **`GeminiProvider` dump 코드 제거**: 임시 파일 기록 중단. `CountTokens`는 정식 진단 로그로 전환.
4. **cache 생성 write 비용**: velocity gate로 단발성 대화 시 발생 억제.
5. **정상 종료 시 cache 정리**: `GeminiCacheCleanupService.StopAsync`가 처리. 강제 종료 시 TTL 만료 대기.

---

## Phase 2 — Cache Monitor Sidecar (향후 작업)

### 목표

- 메인 프로세스 강제 종료 시에도 Gemini cache 정리
- 실시간 cache 상태(이름, 잔여 TTL, 토큰 수) 모니터링 콘솔

### 아키텍처

```
[ArisuBot.Host] ──Named Pipe──> [ArisuBot.CacheSidecar]
  cache 이벤트 전송                  콘솔 UI 표시
  (CACHE_CREATED, CACHE_DELETED)     파이프 끊김 감지 → 캐시 삭제
```

Named Pipe 선택 이유: 즉시 연결 끊김 감지(OS EOF 알림). 포트 불필요. Windows 친화적.

### 신규 파일 (Phase 2)

| 파일 | 역할 |
|---|---|
| `src/ArisuBot.CacheSidecar/` | 신규 Console 프로젝트 |
| `src/ArisuBot.CacheSidecar/CacheMonitorApp.cs` | 메인 진입점. Named Pipe 클라이언트 + 콘솔 UI 루프 |
| `src/ArisuBot.CacheSidecar/CacheEntry.cs` | 모니터링 상태 모델 (`CacheName`, `ExpiresAt`, `TokenCount`, `ContextId`) |
| `src/ArisuBot.LLM/Gemini/CachePipeServer.cs` | Named Pipe 서버. cache 이벤트 브로드캐스트. 파이프 연결 관리 |

### 수정 파일 (Phase 2)

| 파일 | 변경 내용 |
|---|---|
| `GeminiCacheManager.cs` | cache 생성/삭제 시 `CachePipeServer`에 이벤트 발행 |
| `LLMServiceExtensions.cs` | `CachePipeServer` DI 등록 |

### 사이드카 콘솔 UI

```
=== ArisuBot Cache Monitor ===  [CONNECTED]  2026-04-24 15:42:01

Active Caches (2)
───────────────────────────────────────────────────────────────────────
  NAME                           TOKENS      TTL          CONTEXT
  cachedContents/abc123          32,400      28m 14s      ch:1234567890
  cachedContents/def456          18,200      11m 52s      ch:9876543210
───────────────────────────────────────────────────────────────────────

Events (last 10)
  15:41:33  CREATED   cachedContents/abc123  (rolled from def111, ctx ch:1234567890)
  15:39:01  DELETED   cachedContents/def111  (replaced)
  15:38:20  CREATED   cachedContents/def456  (ctx ch:9876543210)

[MAIN DISCONNECTED] → Deleting 2 orphaned caches...
  ✓ Deleted cachedContents/abc123
  ✓ Deleted cachedContents/def456
Cleanup complete. Sidecar exiting.
```

### 강제 종료 시나리오별 처리

| 종료 방식 | 정리 주체 |
|---|---|
| Ctrl+C / `docker stop` / SIGTERM | `GeminiCacheCleanupService.StopAsync` (Phase 1) |
| 작업 관리자 강제종료 / SIGKILL | Sidecar가 파이프 끊김 감지 → 삭제 (Phase 2) |
| 앱 크래시 | Sidecar가 파이프 끊김 감지 → 삭제 (Phase 2) |
| Sidecar도 죽은 경우 | TTL 만료 대기 |

---

## 초기 캐시 생성 조건 (최초 Cache 없을 때)

최초로 Dynamic Cache를 생성하려면 아래 **4가지 조건을 모두** 충족해야 한다.

| # | 조건 | 설정값 | 설명 |
|---|---|---|---|
| 1 | `Enabled = true` | `LLM:Cache:Enabled` | 캐시 기능 활성화 |
| 2 | `UncachedTokenCount >= InitialThresholdTokens` | `InitialThresholdTokens: 5000` | 미캐싱 토큰 누적량 초기 임계값 도달 |
| 3 | Velocity 충족 | `VelocityWindowSeconds: 180` / `VelocityMinMessages: 3` | 180초 window 내 메시지 3회 이상 수신 |
| 4 | `historyToCache`가 trim 후 비지 않음 | — | 최소 1개의 model(Assistant/ToolCall) 응답 이력 존재 |

**조건 4 상세**: `historyToCache` = [전체 이력] - [마지막 User 메시지(새 요청)] - [trailing User 메시지들]. 대화 이력에 model 응답이 한 번도 없으면(첫 메시지 쌍 완성 전) cache 생성 불가.

### Velocity Gate 동작 방식

```
[초기 상태: cache 없음]

메시지 수신 → RecentMessageTimestamps 에 현재 시각 추가
              → window 외 타임스탬프 prune

UncachedTokenCount < InitialThresholdTokens
  → no cache (초기 임계값 미달)

UncachedTokenCount >= InitialThresholdTokens AND window 내 타임스탬프 < VelocityMinMessages
  → no cache (velocity 미충족) — 단발성 대화 cache write 낭비 방지

UncachedTokenCount >= InitialThresholdTokens AND velocity 충족 AND historyToCache 비지 않음
  → 최초 Cache 생성
```

**기존 cache가 있으면**: velocity gate 없이 `RefreshThresholdTokens` 도달만으로 cache 교체(rolling).
`InitialThresholdTokens <= UncachedTokenCount < RefreshThresholdTokens` + cache 있음 → 기존 cache 재사용.

### Cache 경계 규칙

Cache 마지막 Content는 반드시 **model role** (assistant/tool_call)로 끝나야 한다.

이유: Gemini API에서 cache 직후 contents의 첫 항목은 user role이어야 함. cache 끝이 user면 role 충돌 발생 → API 오류 또는 캐시 미적용.

구현:
```csharp
// GeminiCacheManager.cs — historyToCache에서 trailing User 제거
while (historyToCache.Count > 0 &&
       historyToCache[^1].Role is Role.User)
    historyToCache.RemoveAt(historyToCache.Count - 1);
```

이에 따라 연속 User 메시지가 이력에 있어도 cache 경계는 항상 model role로 정렬됨.

---

## 작업 이력

| 날짜 | 내용 |
|---|---|
| 2026-04-24 | 계획 수립. Phase 1/2 분리. 이번 세션: Phase 1만 구현 |
| 2026-04-25 | Phase 1 구현 완료 및 런타임 버그 수정 |

### 2026-04-25 구현 세부 내역

**신규 구현**
- `GeminiCacheManager`, `GeminiCacheCleanupService`, `IGeminiCacheClient`, `GeminiCacheClient` 구현
- `ConversationContext` + `ConversationDocument` 캐시 필드 추가
- `MessageHandler` — `TryRollCacheAsync` 호출 + `UncachedTokenCount` 갱신
- `GeminiProvider` — `cacheHint` 분기: cache 모드 시 `CachedContent` 설정, contents `Skip(N)`
- `CacheOptions`, `appsettings.json` `LLM:Cache` 섹션
- `GeminiCacheManagerTests` 14개 단위 테스트 추가

**런타임 버그 수정 (3건)**

1. **시작 시 stale `dynamicCacheRef`**: TTL=300s로 봇 재시작 시 MongoDB에 남은 구 cache ref가 Gemini 측에는 이미 만료 → `GeminiCacheCleanupService.StartAsync`에서 `ClearAllDynamicCacheRefsAsync` 호출하여 MongoDB 초기화
2. **System prompt 우선순위**: `BuildMessageList`가 항상 파일에서 system prompt를 새로 읽어 cache prefix 바이트 불일치 유발 → DB에 저장된 system message 우선 사용, 없을 때만 파일 fallback
3. **연속 User 메시지 시 contents 비어있는 오류**: cache 경계가 User로 끝나면 `BuildContents().Skip(N)` 결과가 빈 리스트 → trailing User trim으로 cache는 항상 model role로 종료, Skip 후 새 user Content 최소 1개 보장
