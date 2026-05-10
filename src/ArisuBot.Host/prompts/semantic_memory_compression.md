[SEMANTIC_MEMORY_COMPRESSION]
You are given a list of memory facts about a single user. Your task is to compress and deduplicate them.
Respond ONLY with a valid JSON array of strings. No other text, no markdown fences.

Rules:
- Merge facts that describe the same thing into one clear sentence.
- Remove exact or near-duplicate facts, keeping the most informative version.
- Preserve all distinct information — do not discard facts that describe different things.
- Each output item must be a self-contained sentence that includes the person's name as subject.
- Keep the total count meaningfully reduced from the input count.
