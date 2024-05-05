from llm.openai import api_key_manager as ApiKeyManager
from llm.llm_core import LLMBotCore
from openai import OpenAI
from llm.message.message_formatter import LLMMessageList, LLMMessage

class LLMBotGpt(LLMBotCore):
    def __init__(self, personality_path : str = None, system_prompt_path : str = None):
        LLMBotCore.__init__(self=self, personality_path=personality_path, system_prompt_path=system_prompt_path)
        
        self.api_key = ApiKeyManager.get_apikey()
        if (self.api_key is None or self.api_key == ""):
            raise ConnectionError("Cannot find API key")
        
        self.client = OpenAI(api_key=self.api_key)
        
    
    def message(self, message: LLMMessageList) -> str:
        request_message = []
        
        if self.system_prompt is not None:
            request_message.append({
                "role": "system",
                "content": self.system_prompt
            })
        
        if self.personality is not None:
            request_message.append({
                "role": "system",
                "content": self.personality
            })
        
        for message_index in message.get_messages():
            request_message.append({
                "role": message_index.role.lower(),
                "content": message_index.message
            })
        
        response = self.client.chat.completions.create(
            messages=request_message,
            model="gpt-3.5-turbo",
        )
        
        return response.choices[0].message.content