# Gemini Fallback Model 설계 계획

날짜: 2026-04-23  
작성자: Claude Code  
상태: **미구현 (설계만 완료)**

---

## 1. 배경

Gemini API 서버 과부하 시 `Google.GenAI.ServerError: This model is currently experiencing high demand` 발생. 현재 구조에서는 LLM iteration이 즉시 중단되어 사용자에게 오류만 반환됨. Fallback 모델로 자동 전환해 응답 연속성 유지 필요.

---

## 2. 요구사항

1. **Iteration 단위** fallback — tool 호출 중간(예: FunctionCall 처리 후 다음 스트리밍 요청 시)에도 전환 가능
2. Fallback 모델 미설정 시 생성 중단 (throw)
3. 전환 시 `LogWarning`
4. 동일 모델 재시도 횟수(`StreamRetryCount`)를 `appsettings.json`에서 설정

---

## 3. 설정 구조

### `appsettings.json` 추가 항목

```json
"Gemini": {
  "Model": "gemini-2.5-pro",
  "FallbackModel": "gemini-2.0-flash",
  "StreamRetryCount": 1
}
```

### `GeminiOptions.cs` 추가 필드

```csharp
/// <summary>주 모델 ServerError 시 전환할 fallback 모델. null이면 fallback 없이 즉시 실패.</summary>
public string? FallbackModel { get; set; }

/// <summary>동일 모델 재시도 횟수. 초과 시 FallbackModel로 전환. 0이면 즉시 전환.</summary>
public int StreamRetryCount { get; set; } = 1;
```

---

## 4. 동작 흐름

```
GenerateAsync() 시작
  currentModel = _geminiOptions.Model

foreach iteration:
  retries = 0
  loop:
    try:
      StreamAsync(currentModel, ...)
      → 정상 처리
    catch ServerError:
      retries++
      if retries <= StreamRetryCount:
        LogWarning("Stream failed, retrying — model={Model} attempt={Retries}")
        continue (동일 모델 재시도)
      else:
        if FallbackModel is null:
          throw  ← 생성 중단
        LogWarning("Switching to fallback model — from={Primary} to={Fallback}")
        currentModel = FallbackModel
        retries = 0
        continue (fallback 모델로 재시도)
```

**Sticky 전환**: fallback 전환 후 이후 모든 iteration은 fallback 모델 유지.

---

## 5. 수정 범위 (구현 시)

| 파일 | 변경 |
|------|------|
| `LLM/Options/GeminiOptions.cs` | `FallbackModel`, `StreamRetryCount` 추가 |
| `LLM/Gemini/GeminiProvider.cs` | `currentModel` 변수화, iteration 내 `ServerError` catch → retry → fallback 전환 |
| `Host/appsettings.json` | `FallbackModel`, `StreamRetryCount` 설정 추가 |

### 테스트 (구현 시 추가)

`GeminiProviderToolLoopTests`:
- `GenerateAsync_ServerError_RetriesUpToCount_ThenSwitchesToFallback`
- `GenerateAsync_ServerError_NoFallbackConfigured_Throws`
- `GenerateAsync_ServerError_FallbackStickyAcrossIterations`

---

## 6. 사이드 이펙트

- `StreamRetryCount = 0` 설정 시 retry 없이 즉시 fallback 전환
- `ServerError` 외 다른 예외(`HttpException` 등)는 기존대로 propagate
- fallback 전환 후 `LLMResponse`에 사용된 모델 정보 포함 고려 필요 (현재 `ProviderName`만 있음)
