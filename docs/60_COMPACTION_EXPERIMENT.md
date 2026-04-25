# Compaction 실험 구현 기록

## 배경

대화 컨텍스트가 누적되면 오래된 메시지가 `TakeLast(maxHistory)`로 소실된다.
장기 기억 보존을 위해 Compaction(압축) 기능 도입을 검토하였으며,
접근 방식 검증을 위한 실험 코드를 먼저 구현한다.

## 실험 목적

기존 RP 대화 세션에서 Compaction trigger를 발생시켜 LLM이 다음을 수행하는지 검증:
- 페르소나를 유지한 채로 JSON 추출 지시를 따르는지
- 대화 맥락을 정확히 반영한 facts/summary를 생성하는지
- 추출 품질이 실제 장기 기억으로 활용 가능한 수준인지

## 구현 방식

### Trigger 조건

`context.LastTotalTokens >= CompactionOptions.TokenThreshold`

마지막 LLM 요청의 총 토큰 수(tokensIn + tokensOut)가 임계값 이상이면 trigger.
기본값: 10,000 토큰.

### 호출 구조

기존 대화 컨텍스트 전체를 그대로 사용하고, compaction.md 내용을 `Role.User` 메시지로 append한다.
System/Persona 등 기존 systemInstruction은 교체하지 않는다.

```
[System]          ← 기존 Persona systemInstruction 유지
[Persona User]    ← 기존 그대로
[User/Asst ×N]   ← 기존 대화 전체
[Asst]            ← 마지막 응답
[User, SenderName=null]  ← compaction.md trigger 메시지
  → LLM 응답: JSON 추출 결과 (또는 Persona 유지 응답)
```

결과는 `ILogger`로만 기록하며 DB에 저장하지 않는다.

### 검증 포인트

| 항목 | 확인 내용 |
|------|-----------|
| JSON 형식 | 유효한 JSON 반환 여부 |
| Persona 영향 | 캐릭터 말투가 facts/summary에 섞이는지 |
| 추출 품질 | 의미 있는 사실이 추출되는지, 잡담이 걸러지는지 |
| 빈 facts | 추출할 내용 없는 대화에서 빈 배열 반환 여부 |

## 변경 파일

| 파일 | 내용 |
|------|------|
| `src/ArisuBot.Core/Options/CompactionOptions.cs` | Enabled, TokenThreshold 옵션 |
| `src/ArisuBot.Core/Interfaces/IPromptLoader.cs` | CompactionPrompt 속성 추가 |
| `src/ArisuBot.Infrastructure/Prompts/FilePromptLoader.cs` | compaction.md 로드 |
| `src/ArisuBot.Host/prompts/compaction.md` | 영어 trigger 메시지 |
| `src/ArisuBot.Host/appsettings.json` | Compaction 섹션 추가 |
| `src/ArisuBot.Host/Program.cs` | CompactionOptions 바인딩 |
| `src/ArisuBot.Discord/Handlers/MessageHandler.cs` | trigger 체크 + RunCompactionExperimentAsync |

## 실험 결과

- JSON 형식: 유효한 JSON 정상 반환 ✅
- Persona 영향: facts/summary에 캐릭터 말투 유입 없음 ✅
- 추출 품질: 의미 있는 사실 추출, 잡담 필터링 ✅
- enriched schema (facts+summary 동시 추출) 검증 완료 ✅

결론: **품질 충분** → 정식 구현 진행.

## 다음 단계

실험 완료. 정식 Compaction 구현은 `docs/61_COMPACTION_PLAN.md` 참조.
