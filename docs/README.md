# 문서 Index

| 파일 | 제목 | 설명 |
|------|------|------|
| [00_ARCHITECTURE.md](00_ARCHITECTURE.md) | 아키텍처 | 전체 시스템 구조, 기술 스택, 프로젝트 레이아웃 |
| [20_TOOL_SPEC.md](20_TOOL_SPEC.md) | LLM Tool 명세 | Tool 요청/응답 구조, GUID 관리, 인터페이스 설계, 구현 순서 |
| [50_LLM_INTERVIEW.md](50_LLM_INTERVIEW.md) | LLM 연동 인터뷰 | Gemini 연동 설계 결정 사항 및 확정 스펙 |
| [51_LOGGING_PLAN.md](51_LOGGING_PLAN.md) | 로깅 개선 계획 | MessageHandler·GeminiProvider 로그 추가 및 silent exception 개선 |
| [52_FALLBACK_MODEL_PLAN.md](52_FALLBACK_MODEL_PLAN.md) | Fallback 모델 설계 | Gemini ServerError 시 fallback 모델 자동 전환 설계 (미구현) |
| [53_THOUGHT_SIGNATURE_PRESERVATION.md](53_THOUGHT_SIGNATURE_PRESERVATION.md) | thought_signature 보존 | ProviderMetadataJson 도입으로 thinking 모델 reasoning state 영속화 |
