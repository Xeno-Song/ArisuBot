# Compaction 정식 구현 계획 및 기록

## 배경

`60_COMPACTION_EXPERIMENT.md` 실험 결과 품질이 충분하다고 판단하여 정식 구현 진행.
Compaction 목적: 대화 컨텍스트가 token 한계에 가까워지면 LLM으로 facts/summary를 추출하고
새 session을 시작해 연속성을 유지한다.

## 트리거 조건 (OR 로직, 각 독립 활성화)

| 트리거 | 설정 키 | 기본값 |
|--------|---------|-------|
| Token 기반 | `TokenThresholdEnabled` + `TokenThreshold` | true / 150000 |
| 비활성 시간 | `InactivityEnabled` + `InactivityMinutes` | false / 1440분 |
| 특정 UTC 시각 | `ScheduledEnabled` + `ScheduledTimeUtc` | false / "03:00" |

- `CooldownMinutes`: 마지막 Compaction 후 재실행 금지 최소 시간 (기본 60분)
- Token 트리거: `MessageHandler` 인라인 처리 (fire-and-forget)
- Inactivity / Scheduled 트리거: `CompactionBackgroundService` 1분 주기 처리

## 실행 흐름

```
[Trigger 감지]
  ↓
CompactionService.RunAsync(context)
  ├─ 1. context.Messages + CompactionPrompt → LLM 호출 (responseSchema 강제)
  ├─ 2. JSON 파싱 → CompactionResult { Facts[], Summary }
  ├─ 3. old session 저장: CompactionSummary, CompactionFacts, LastCompactedAt
  └─ 4. new session 생성:
         [System]            ← old session 원본 (캐시 prefix 보존)
         [User, Persona]     ← old session 원본 (캐시 prefix 보존)
         [Assistant]         ← synthetic Compaction 요약 주입
         [최근 N 메시지]     ← InjectionRecentMessageCount 설정값
  ↓
new ConversationContext 반환
```

## IsProtected 규칙

`Role.System || (Role.User && SenderName == null)` → 새 session 선두에 복사, 압축 대상 제외.

## responseSchema

`CompactionService`에 JSON Schema 상수(`CompactionResponseSchema`)로 정의.
Gemini API `ResponseJsonSchema` 필드 활용 — `ResponseMimeType = "application/json"` 함께 설정.

## Discord 슬래시 커맨드

| 커맨드 | 기능 |
|--------|------|
| `/compact` | 현재 채널/DM session 즉시 Compaction 실행 |
| `/restore-session <session_id>` | MongoDB ObjectId로 과거 session 복원 (보안: targetId 일치 확인) |

## 변경 파일

| 파일 | 내용 |
|------|------|
| `src/ArisuBot.Core/Options/CompactionOptions.cs` | 트리거 3종 + 주입 설정 확장 |
| `src/ArisuBot.Core/Models/CompactionResult.cs` | CompactionFact, CompactionResult record |
| `src/ArisuBot.Core/Models/ConversationContext.cs` | CompactionSummary, CompactionFacts, LastCompactedAt 필드 추가 |
| `src/ArisuBot.Core/Interfaces/ILLMProvider.cs` | responseSchema 파라미터 추가 |
| `src/ArisuBot.Core/Interfaces/ICompactionService.cs` | 신규 인터페이스 |
| `src/ArisuBot.Core/Interfaces/IConversationRepository.cs` | GetContextByIdAsync, GetAllSessionsAsync, GetAllActiveContextsAsync 추가 |
| `src/ArisuBot.Core/Services/CompactionService.cs` | 신규 — 5단계 Compaction 실행 |
| `src/ArisuBot.Core/Services/CompactionTriggerEvaluator.cs` | 신규 — 트리거 조건 평가 |
| `src/ArisuBot.Core/Services/ConversationService.cs` | RestoreSessionAsync 추가 |
| `src/ArisuBot.Infrastructure/MongoDB/Documents/ConversationDocument.cs` | Compaction 필드 추가 |
| `src/ArisuBot.Infrastructure/MongoDB/ConversationRepository.cs` | GetContextByIdAsync 등 구현 |
| `src/ArisuBot.LLM/Gemini/GeminiProvider.cs` | responseSchema → ResponseJsonSchema 주입 |
| `src/ArisuBot.Discord/Handlers/MessageHandler.cs` | Experiment 제거, 정식 Token 트리거 |
| `src/ArisuBot.Discord/Commands/CompactCommand.cs` | 신규 슬래시 커맨드 |
| `src/ArisuBot.Discord/Commands/RestoreSessionCommand.cs` | 신규 슬래시 커맨드 |
| `src/ArisuBot.Host/BackgroundServices/CompactionBackgroundService.cs` | 신규 BackgroundService |
| `src/ArisuBot.Host/Program.cs` | DI 등록 추가 |
| `src/ArisuBot.Host/appsettings.json` | CompactionOptions 확장 |

## 테스트

| 파일 | 커버 대상 |
|------|----------|
| `CompactionTriggerEvaluatorTests.cs` | 트리거 3종 ON/OFF, OR 로직, Cooldown |
| `CompactionServiceTests.cs` | LLM 호출, JSON 파싱, new session 구조 |
| `ConversationDocumentMappingTests.cs` | Compaction 필드 직렬화/역직렬화 |

최종 테스트: 206개 통과 (기존 181 + 신규 25).
