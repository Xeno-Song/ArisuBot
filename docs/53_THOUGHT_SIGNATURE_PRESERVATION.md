# thought_signature 보존 작업 기록

날짜: 2026-04-24
작성자: Claude Code

---

## 1. 배경

Gemini thinking 모델(`gemini-2.5-flash` 등) 응답에는 텍스트·FunctionCall 외에 thought Part가 포함된다.
thought Part에는 `thought_signature` 등 reasoning 상태가 인코딩되어 있으며, 다음 호출 시 동일한 model
Content prefix를 재현해야 implicit cache hit가 가능하다.

기존 구현은 `LLMResponse.Content` (텍스트만)를 DB에 저장했기 때문에, `BuildContents` 복원 시
model Content가 text Part 1개만 가지게 되어 prefix hash가 달라졌다.

### 증상

- DB에 저장되는 `tokensCachedIn`이 항상 0
- 동일 컨텍스트 반복 호출에도 cache hit 없음
- thinking 모델의 reasoning context 단절 가능

---

## 2. 변경 사항

### 데이터 모델

- `ChatMessage.ProviderMetadataJson` 추가 (string?, Role.Assistant/ToolCall에 설정)
- `LLMResponse.ProviderMetadataJson` 추가 (Provider → MessageHandler 전달용)
- `ChatMessageDocument.ProviderMetadataJson` BSON 매핑 추가 (`providerMetadataJson`)

### Provider

- `GeminiProvider.GenerateAsync`
  - non-text/non-FunctionCall Parts → `JsonSerializer.Serialize(thoughtParts)`
  - 최종 yield: `LLMResponse.ProviderMetadataJson`에 직렬화 결과 포함
  - 한 iteration 내 다중 FunctionCall: 첫 ToolCall 메시지에만 metadata 첨부 (중복 방지)
- `GeminiProvider.BuildContents`
  - Assistant: text Part 다음에 `DeserializeMetadataParts` 결과 추가
  - ToolCall: metadata Part → FunctionCall 순서로 재구성

### Handler

- `MessageHandler`: Assistant 메시지 저장 시 `responses.LastOrDefault()?.ProviderMetadataJson` 함께 저장

---

## 3. 직렬화 형식

`List<Part>`를 `JsonSerializer.Serialize`로 직렬화. 역직렬화 대칭 — `JsonSerializer.Deserialize<List<Part>>`.

빈 리스트는 `null` 저장. BSON `[BsonIgnoreExtraElements]`로 레거시 문서 호환.

---

## 4. 테스트

`tests/ArisuBot.Tests.Unit/`:

- `LLM/GeminiProviderGenerateTests`
  - `GenerateAsync_PopulatesProviderMetadataJson_WhenThoughtPartPresent`
  - `GenerateAsync_ProviderMetadataJson_IsNullWhenOnlyText`
  - `GenerateAsync_ToolLoop_AttachesProviderMetadataJson_ToFirstToolCallOnly`
  - `BuildContents_AssistantWithMetadata_RestoresPartsAfterText`
  - `BuildContents_AssistantWithoutMetadata_OnlyTextPart`
  - `BuildContents_ToolCallWithMetadata_RestoresPartsBeforeFunctionCall`
  - `BuildContents_UserMessage_IgnoresProviderMetadataJson`
- `Infrastructure/ConversationDocumentMappingTests`
  - `FromDomain_MapsProviderMetadataJson_WhenSet`
  - `ToDomain_MapsProviderMetadataJson_WhenSet`
  - `ToDomain_ProviderMetadataJson_IsNullWhenNotInDocument`

전체 통과: 125/125.

---

## 5. 검증 잔여

실 환경 cache hit 발생 여부는 운영 로그(`tokensCachedIn > 0`) 확인 필요.
디버그용 `CountTokensAsync` 호출은 기능 검증 후 제거 예정.

---

## 6. 관련 파일

- `src/ArisuBot.Core/Models/ChatMessage.cs`
- `src/ArisuBot.Core/Models/LLMResponse.cs`
- `src/ArisuBot.Infrastructure/MongoDB/Documents/ConversationDocument.cs`
- `src/ArisuBot.LLM/Gemini/GeminiProvider.cs`
- `src/ArisuBot.Discord/Handlers/MessageHandler.cs`

---

## 7. 테스트용 임시 변경: consecutive same-role merge

### 동기

implicit cache hit 비율 저조 원인 추적 중, Content 단위 구조가 prefix hash 에 영향을
주는지 확인하기 위한 검증 변경.

### 변경 내용

`GeminiProvider.BuildContents` 최종 반환 직전 `MergeConsecutiveSameRole` 호출.
인접 Content 가 같은 Gemini role(`user`/`model`) 이면 뒤 Content.Parts 를 앞 Content
뒤에 append 하여 하나로 병합.

- DB 저장 로직 변경 없음
- ProviderMetadataJson 직렬화 포맷 변경 없음
- SenderName prefix 생성 그대로
- 병합 범위: `ToolCall`(model) + `ToolResponse`(user) 교대 구조는 role 달라 영향 없음.
  `Assistant`(text) + `ToolCall` 동시 생성 시 둘 다 model → 병합됨
  (Parts 순서: text → FunctionCall)

### 롤백

`BuildContents` 끝의 `return MergeConsecutiveSameRole(result);` 를
`return result;` 로 되돌리고 `MergeConsecutiveSameRole` 메서드와 관련 테스트 5개 삭제.

### 추가된 테스트

- `BuildContents_ConsecutiveUserMessages_MergedIntoSingleContent`
- `BuildContents_ConsecutiveAssistantMessages_MergedIntoSingleContent`
- `BuildContents_AlternatingRoles_NotMerged`
- `BuildContents_AssistantThenToolCall_MergedAsModelRole`
- `BuildContents_ToolCallThenToolResponse_NotMerged`

---

## 8. 테스트용 임시 변경: 연속 pure-text Part 병합

### 동기

LLM 응답이 토큰 경계나 스트리밍 경계에서 단일 문장이 여러 `{"text": "..."}` Part 로
쪼개져서 저장되는 경우가 관찰됨 (예: `"[고"`, `"개를 끄덕이며]..."` 등).
Part 개수 증가 → prefix hash 불안정 → implicit cache miss 원인 의심.

### 변경 내용

`GeminiProvider` 에 `MergeConsecutiveTextParts(IList<Part>)` 와 `IsPureTextPart(Part)`
추가. `MergeConsecutiveSameRole` 가 각 Content 의 Parts 를 이 함수로 후처리.

### 병합 조건 (IsPureTextPart)

`Text` 만 세팅되고 다른 모든 필드 (`FunctionCall`, `FunctionResponse`, `InlineData`,
`FileData`, `ExecutableCode`, `CodeExecutionResult`, `VideoMetadata`, `MediaResolution`,
`ToolCall`, `ToolResponse`, `Thought`, `ThoughtSignature`, `PartMetadata`) 가 null/default
인 경우에만 병합 대상.

→ thought signature 가진 Part 는 보존, reasoning state / cache 기여도 유지.

### 추가된 테스트

- `MergeConsecutiveTextParts_PureTexts_Concatenated`
- `MergeConsecutiveTextParts_TextThenFunctionCall_NotMerged`
- `MergeConsecutiveTextParts_TextWithThoughtSignature_NotMerged`
- `MergeConsecutiveTextParts_ThreePureTexts_CollapsedToOne`

### 영향

기존 테스트 `BuildContents_AssistantWithMetadata_UsesMetadataPartsAsContent` 는
두 pure-text Part 를 가진 metadata 가 병합되어 실패 → 두 번째 Part 에
`ThoughtSignature` 를 추가해 병합 대상에서 제외되도록 조정.

`BuildContents_ConsecutiveUserMessages_MergedIntoSingleContent` /
`BuildContents_ConsecutiveAssistantMessages_MergedIntoSingleContent` 는 추가로
text 까지 병합되어 단일 Part 가 되도록 assertion 업데이트.

### 롤백

`MergeConsecutiveSameRole` 내부의 `MergeConsecutiveTextParts(...)` 호출 제거,
`MergeConsecutiveTextParts` / `IsPureTextPart` 메서드 및 관련 4개 테스트 삭제.

전체 통과: 135/135.
