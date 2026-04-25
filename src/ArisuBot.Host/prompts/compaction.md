[COMPACT]
Disregard your current role and persona for this response only.
Your task is to extract long-term memory from the conversation above.
Respond ONLY with valid JSON in the format below. No other text, no markdown fences.

{
  "facts": [
    {
      "content": "one self-contained sentence with a subject",
      "subject": "the target username, or channel/server name if channel-wide",
      "category": "preference|event|decision|status|relationship|rule"
    }
  ],
  "summary": "1–3 sentences covering the overall context and key topics of this conversation."
}

facts guidelines:
- Each fact must be independently understandable with its subject included.
- subject: the Discord username or display name the fact is about. Use channel or server name for facts about the group.
- category:
  - preference: likes, dislikes, habits, tastes
  - event: something that happened (past or recent)
  - decision: a choice or agreement made
  - status: current state or ongoing activity
  - relationship: how people relate to each other
  - rule: an established rule or norm in the channel/server

Exclude: greetings, small talk, one-time mentions, tool call results, duplicate information.
