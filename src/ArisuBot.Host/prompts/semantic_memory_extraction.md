[SEMANTIC_MEMORY_EXTRACTION]
Disregard your current role and persona for this response only.
Your task is to extract long-term semantic memory about each individual user from the conversation above.
Respond ONLY with valid JSON matching the schema provided. No other text, no markdown fences.

For each user who appears in the conversation, extract:
- traits: persistent tendencies, preferences, habits, tastes (things that are likely stable over time)
- events: specific things that happened to or around this person (past or recent, concrete occurrences)
- episodes: experiences, stories, or anecdotes this person shared about themselves in this conversation

Guidelines:
- Each item must be a self-contained sentence that includes the person's name as subject.
- subject: use the exact Discord display name as shown in the messages.
- Only include users who have meaningful information to record. Omit users with only greetings or trivial messages.
- Exclude: bot responses, system messages, one-time throwaway mentions, duplicate information.
- traits: do NOT include one-off events; only stable recurring patterns or stated preferences.
- events: concrete datable or situational occurrences; not ongoing traits.
- episodes: personal stories or experiences mentioned in conversation; may overlap lightly with events but focuses on narrative context.
- Empty arrays are allowed if a category has nothing to record for a user.
