# 로깅 개선 계획

날짜: 2026-04-23  
작성자: Claude Code

---

## 배경

운영 중 로그가 빈약해 실행 여부·실패 지점 식별이 불가능한 문제 제기.  
`MessageHandler`, `GeminiProvider` 두 핵심 경로에 로그가 전무하거나 미흡함.  
또한 단일 `catch` 블록이 모든 단계 실패를 동일 메시지로 기록해 진단 불가.

---

## Phase 1 — MessageHandler + GeminiProvider 로그 추가

### 수정 범위

| 파일 | 변경 내용 |
|------|----------|
| `src/ArisuBot.LLM/ArisuBot.LLM.csproj` | `Microsoft.Extensions.Logging.Abstractions 10.0.7` 패키지 추가 |
| `src/ArisuBot.LLM/Gemini/GeminiProvider.cs` | `ILogger<GeminiProvider>` 생성자 주입, 로그 추가 |
| `src/ArisuBot.Discord/Handlers/MessageHandler.cs` | 로그 추가 |

### 추가할 로그

**MessageHandler.cs**

| 위치 | 레벨 | 내용 |
|------|------|------|
| 라우팅 완료 후, `TriggerTypingAsync` 전 | `Debug` | `messageId`, `userId`, `channelId`, `contextType` |
| `GenerateAsync` 호출 전 | `Information` | `channelId`, `userId`, `toolsEnabled`, `messageCount` |
| `GenerateAsync` 반환 후 | `Information` | `tokensIn`, `tokensOut`, `responseLength` |
| Discord 전송 전 | `Debug` | `channelId`, 청크 수 |
| catch 메시지 | — | `"LLM 호출 실패"` → `"메시지 처리 실패"` |

**GeminiProvider.cs**

| 위치 | 레벨 | 내용 |
|------|------|------|
| `GenerateAsync` 진입 | `Debug` | `model`, `messageCount`, `toolCount` |
| 각 iteration 시작 | `Debug` | `iteration` 번호 |
| FunctionCall 감지 | `Information` | 감지된 툴 이름 목록 |
| 각 툴 실행 완료 | `Information` | `toolName`, 결과 길이 |
| 최종 텍스트 반환 | `Information` | `totalTokensIn`, `totalTokensOut`, `textLength` |

### 사이드 이펙트

- `GeminiProvider` 생성자 변경 → 기존 테스트에 `Mock<ILogger<GeminiProvider>>` 추가 필요

### 테스트 코드

- 기존 테스트 수정: 생성자 `Mock<ILogger<GeminiProvider>>` 추가

---

## Phase 2 — Silent Exception Consumption 개선

### 수정 범위

| 파일 | 변경 내용 |
|------|----------|
| `src/ArisuBot.Discord/Handlers/MessageHandler.cs` | `phase` 추적 변수 추가, catch 로그 개선 |

### 구체적 변경

단일 `catch` 블록에 `phase` 변수를 추가해 실패 지점 식별.

```csharp
var phase = "컨텍스트 로드";
try
{
    var context = await ...GetContextAsync(...);
    phase = "초기 메시지 저장";
    // ...
    phase = "LLM 호출";
    var responses = await _llmProvider.GenerateAsync(...);
    phase = "응답 저장";
    // ...
    phase = "Discord 전송";
    // ...
}
catch (Exception ex)
{
    _logger.LogError(ex, "메시지 처리 실패 [{Phase}] — channelId={ChannelId} userId={UserId}",
        phase, ...);
}
```

### AdminNotifier.cs

무한루프 방지용 의도적 설계 → **변경 없음**.

---

## 진행 상태

| Phase | 상태 |
|-------|------|
| Phase 1 | ✅ 완료 |
| Phase 2 | ✅ 완료 |
