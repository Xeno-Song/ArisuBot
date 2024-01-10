from llm.openai.llm_gpt import LLMBotGpt
from llm.message.message_formatter import LLMMessageList, LLMMessage


class Arisu:
    def __init__(self, personality_path: str, system_prompt_path: str, log_path: str = None):
        self.personality_path = personality_path
        self.system_prompt_path = system_prompt_path
        self.log_path = log_path
        self.configure()
        
        
    def configure(self):
        self.llm = LLMBotGpt(
            personality_path=self.personality_path,
            system_prompt_path=self.system_prompt_path)
        
    
    def run(self):
        message_list = LLMMessageList()
        message_list.push(LLMMessage("user", "Hi, arisu. How are you today?"))
        print(f"User: {message_list.get_messages()[0].message}")
        
        response = self.llm.message(message_list)
        print(f"Arisu: {response}")
        