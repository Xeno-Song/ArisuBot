# LLM Fallback 설계 계획

날짜: 2026-04-25  
작성자: Claude Code  
상태: **설계 확정 — 구현 대기**

---

## 1. 배경

Gemini API 사용량 급증(usage spike) 시 두 종류의 에러가 발생한다.

| HTTP | 예외 타입 | 메시지 예시 |
|------|-----------|-------------|
| 5xx (503) | `Google.GenAI.ServerError` | "This model is currently experiencing high demand" |
| 429 | `Google.GenAI.ClientError` | "RESOURCE_EXHAUSTED" / "quota exceeded" |

현재 구조에서는 두 에러 모두 LLM iteration을 즉시 중단하고 `MessageHandler` 상위 catch로 전파된다. 관리자에게만 DM 알림이 가며, 사용자에게는 아무 응답이 없다.

**목표:** 재시도(동일 모델) → fallback 모델 전환 → 전체 실패 시 사용자에게 Discord 메시지 전송.

---

## 2. 요구사항 (확정)

| # | 요구사항 | 결정 |
|---|----------|------|
| 1 | 재시도 트리거 에러 | `ServerError` (5xx)만. `ClientError` (4xx)는 재시도 없이 즉시 실패 |
| 2 | 동일 모델 재시도 횟수 | `StreamRetryCount` (설정 가능) |
| 3 | 재시도 딜레이 | 고정 딜레이 (`RetryDelayMs`), 0 = 즉시 재시도 |
| 4 | Fallback 모델 | `FallbackModel` 설정값 (미설정 시 즉시 throw) |
| 5 | Fallback 전환 시 Cache | `null` 전달 — no-cache 모드로 진행 |
| 6 | Fallback Sticky | 전환 후 이후 모든 iteration은 fallback 모델 유지 |
| 7 | 전체 실패 시 Discord 응답 | 채널 일반 메시지로 실패 안내 전송 |

---

## 3. 에러 타입 처리 방침

### 3-1. 예외 클래스 구조

Google.GenAI SDK v1.6.1 기준:

```
Google.GenAI.ClientError   — 4xx (400, 403, 404, 429 등)
Google.GenAI.ServerError   — 5xx (500, 503 등)
```

### 3-2. 에러별 처리 방침 (확정)

| 에러 | 처리 |
|------|------|
| `ServerError` (5xx) | 재시도 → fallback → 전체 실패 시 사용자 Discord 메시지 |
| `ClientError` (4xx, 429 포함) | **재시도/파싱 없이 즉시 실패**. `GeminiProvider`에서 LogError (exception 전체 포함) 후 re-throw. `MessageHandler`에서 사용자 Discord 메시지 전송 |
| `OperationCanceledException` | 즉시 propagate (취소 신호) |
| 기타 `Exception` | 즉시 propagate (기존 동작 유지) |

> **결정 근거**: `ClientError`는 SDK가 4xx 전체를 단일 타입으로 묶으므로 429 선별 파싱 없이 전부 즉시 실패 처리.  
> 상세 원인은 LogError로 남긴 exception 메시지에서 확인.

### 3-3. `GeminiProvider` 내 `ClientError` 처리 코드 패턴

```csharp
catch (ClientError ex)
{
    _logger.LogError(ex, "Gemini ClientError — model={Model} iteration={Iteration}", currentModel, iteration);
    throw;  // MessageHandler로 propagate
}
```

---

## 4. 설정 구조

### 4-1. `appsettings.json` 추가 항목

```json
"LLM": {
  "Gemini": {
    "Model": "gemini-2.5-flash-lite",
    "FallbackModel": "gemini-2.0-flash",
    "StreamRetryCount": 2,
    "RetryDelayMs": 500
  }
}
```

| 설정 | 타입 | 기본값 | 설명 |
|------|------|--------|------|
| `FallbackModel` | `string?` | `null` | 재시도 소진 후 전환할 모델. `null`이면 fallback 없이 throw |
| `StreamRetryCount` | `int` | `2` | 동일 모델에서 허용하는 재시도 횟수 |
| `RetryDelayMs` | `int` | `500` | 재시도 간 고정 딜레이(ms). `0`이면 즉시 재시도 |

### 4-2. `GeminiOptions.cs` 추가 필드

```csharp
/// <summary>주 모델 실패 시 전환할 fallback 모델. null이면 fallback 없이 즉시 throw.</summary>
public string? FallbackModel { get; set; }

/// <summary>동일 모델 재시도 허용 횟수. 소진 후 FallbackModel로 전환.</summary>
public int StreamRetryCount { get; set; } = 2;

/// <summary>재시도 간 고정 딜레이(ms). 0이면 즉시 재시도.</summary>
public int RetryDelayMs { get; set; } = 500;
```

---

## 5. 동작 흐름

### 5-1. GeminiProvider 내 재시도/Fallback 흐름

```
GenerateAsync() 진입
  currentModel = _geminiOptions.Model
  useCacheHint = cacheHint (원본 보존)

for each iteration (툴 루프):
  retries = 0
  loop:
    try:
      stream = StreamAsync(currentModel, contents, config)
      → 정상 처리 (기존 로직)
      break

    catch (ClientError ex):
      LogError(ex, "Gemini ClientError — model={} iteration={}")
      throw  ← 즉시 MessageHandler로 propagate (재시도 없음)

    catch (ServerError ex):
      → 재시도 대상

    // ServerError 재시도 처리:
    retries++
    if retries <= StreamRetryCount:
      LogWarning("Stream 실패, 동일 모델 재시도 — model={} attempt={} delay={}ms")
      await Task.Delay(RetryDelayMs, ct)
      continue

    // 재시도 소진
    if FallbackModel is null:
      throw  ← 생성 중단 (MessageHandler에서 포착)

    if currentModel == FallbackModel:
      throw  ← fallback도 소진 (동일 결과 방지)

    LogWarning("Fallback 전환 — from={Primary} to={Fallback}")
    currentModel = FallbackModel
    config.CachedContent = null  ← no-cache 강제
    retries = 0
    continue  ← fallback 모델로 즉시 재시도 (딜레이 없음)
```

**Sticky 전환**: 이후 모든 iteration에서 `currentModel = FallbackModel` 유지.  
`CachedContent = null` 초기화는 config 객체 자체에 반영하므로 이후 iteration도 no-cache 유지.

### 5-2. 전체 실패 시 MessageHandler 처리

현재 `MessageHandler.HandleAsync()` top-level catch:
```csharp
catch (Exception ex)
{
    _logger.LogError(ex, "메시지 처리 중 예외 — channelId={} userId={}", ...);
    await _adminNotifier.NotifyAsync(ex, ...);
}
```

변경 후:
```csharp
catch (OperationCanceledException)
{
    throw;  // 취소는 그대로 propagate
}
catch (Exception ex)
{
    _logger.LogError(ex, "메시지 처리 중 예외 — channelId={} userId={}", ...);
    await _adminNotifier.NotifyAsync(ex, ...);

    // 사용자에게 실패 안내 메시지 전송
    await userMessage.Channel.SendMessageAsync(
        "지금은 응답하기 어렵습니다. 잠시 후 다시 말 걸어주세요!");
}
```

> **메시지 내용 확정**: "지금은 응답하기 어렵습니다. 잠시 후 다시 말 걸어주세요!"  
> 채널 일반 메시지 전송 (reply 아님, `messageReference: null`).

---

## 6. 수정 범위

| 파일 | 변경 내용 |
|------|-----------|
| `LLM/Options/GeminiOptions.cs` | `FallbackModel`, `StreamRetryCount`, `RetryDelayMs` 필드 추가 |
| `LLM/Gemini/GeminiProvider.cs` | `currentModel` 변수화, iteration 내 재시도/fallback 로직 추가 |
| `Discord/Handlers/MessageHandler.cs` | top-level catch에 사용자 Discord 메시지 전송 추가 |
| `Host/appsettings.json` | `FallbackModel`, `StreamRetryCount`, `RetryDelayMs` 값 추가 |

---

## 7. 사이드 이펙트

| 항목 | 영향 |
|------|------|
| `GeminiCacheManager` | 수정 불필요. Cache 생성은 generation 이전 완료. Fallback 전환 후 no-cache는 `config.CachedContent = null`로 처리 |
| `AdminNotifier` | 기존 admin DM 알림 유지. User 메시지는 별도 추가 — 중복 아님 |
| Typing indicator | `TriggerTypingAsync`는 `MessageHandler`에서 1회만 호출. 재시도가 길어지면(예: `RetryDelayMs * StreamRetryCount * 2` > ~8초) typing indicator 사라질 수 있음. **현재 미처리 — 필요 시 별도 논의** |
| `LLMResponse.ProviderName` | Fallback 전환 후에도 "Gemini" 고정. 어떤 모델이 실제 응답했는지 로그로만 확인 가능 |
| Tool iteration 진행 중 Fallback | 이미 실행된 FunctionCall contents는 유지. Fallback 모델이 기존 tool 호출 이력을 이해할 수 있어야 정상 동작 가능 |
| `StreamRetryCount = 0` | 재시도 없이 즉시 Fallback 전환 |
| Fallback 모델 미설정 + 에러 | 즉시 throw → MessageHandler catch → 사용자 실패 메시지 |
| Fallback 모델 = Primary 모델 | 무한 루프 방지: `currentModel == FallbackModel`이면 즉시 throw |

---

## 8. 제약 조건

1. **Fallback 모델 기능 호환성**: Fallback 모델이 primary 모델과 동일한 capability를 지원해야 함  
   (예: thinking mode 사용 시 fallback 모델도 thinking 지원 필요).  
   현재 capability 확인 로직 없음 — 설정 오류 시 다른 에러로 실패.

2. **Retry 중 Cancellation**: `Task.Delay(RetryDelayMs, ct)`로 취소 신호 즉시 반영 가능.

3. **ClientError 상세 원인 확인**: 실제 `ClientError` 발생 시 원인(429 vs 400 등)은 LogError에 남는 exception 메시지로 확인.

---

## 9. 테스트 계획

### Unit Tests — `ArisuBot.Tests.Unit`

신규 파일: `GeminiProviderRetryTests.cs`

| 테스트 | 검증 내용 |
|--------|-----------|
| `GenerateAsync_ServerError_RetriesUpToCount_ThenSwitchesToFallback` | StreamRetryCount 소진 후 fallback 전환 확인 |
| `GenerateAsync_ServerError_RetriesSucceedBeforeExhausted` | 재시도 중 성공 시 정상 응답 확인 |
| `GenerateAsync_ServerError_NoFallbackConfigured_Throws` | FallbackModel null 시 exception propagate |
| `GenerateAsync_FallbackStickyAcrossToolIterations` | fallback 전환 후 다음 iteration도 fallback 모델 유지 |
| `GenerateAsync_FallbackModelAlsofails_Throws` | fallback 모델도 실패 시 exception propagate |
| `GenerateAsync_ClientError_PropagatesImmediatelyWithLog` | ClientError 발생 시 재시도 없이 즉시 throw, LogError 호출 확인 |
| `GenerateAsync_FallbackTransition_CacheHintCleared` | fallback 전환 시 config.CachedContent = null 확인 |
| `GenerateAsync_RetryDelay_RespectsConfiguration` | RetryDelayMs 설정값만큼 딜레이 발생 확인 |

기존 수정: `GeminiProviderToolLoopTests.cs`
- 기존 테스트와 신규 retry 로직 간 충돌 없는지 검증

### Unit Tests — `MessageHandlerTests.cs` (기존 또는 신규)

| 테스트 | 검증 내용 |
|--------|-----------|
| `HandleAsync_LLMTotalFailure_SendsUserDiscordMessage` | 전체 실패 시 채널에 실패 메시지 전송 확인 |
| `HandleAsync_LLMTotalFailure_StillNotifiesAdmin` | 실패 시 admin 알림도 여전히 전송되는지 확인 |

### Coverage 목표

| Suite | 현재 요구 |
|-------|-----------|
| Unit Tests | Line/Branch/Method ≥ 90% |
| Integration Tests | Line/Branch/Method ≥ 60% |

`GeminiProvider`와 `MessageHandler` 변경으로 기존 커버리지 영향 없도록 신규 테스트 병행.

---

## 10. 구현 순서

1. `GeminiOptions.cs` — 필드 추가
2. `appsettings.json` — 설정값 추가
3. `GeminiProviderRetryTests.cs` — 실패 테스트 먼저 작성 (TDD)
4. `GeminiProvider.cs` — retry/fallback 로직 구현
5. `MessageHandlerTests.cs` — 실패 메시지 테스트 작성
6. `MessageHandler.cs` — 사용자 실패 메시지 추가
7. 전체 테스트 실행 및 커버리지 확인
