import json
import os
from llm.message.message_formatter import LLMMessage, LLMMessageList

class LLMBotCore:
    def __init__(self, personality_path: str=None, system_prompt_path: str=None):
        self.personality = None
        self.system_prompt = None
        
        if (personality_path is not None):
            self.load_personality(personality_path)
        if (system_prompt_path is not None):
            self.load_system_prompt(system_prompt_path)
        
        
    def load_personality(self, personality_path: str):
        if (os.path.exists(personality_path) is False):
            raise FileNotFoundError("Cannot find personality file")
        
        with open(personality_path, 'r') as file:
            personality = json.load(file)
        if "name" not in personality:
            raise IndexError("AI name is not exists in personality file")
        if "personality" not in personality:
            raise IndexError("Personality is not exists in personality file")
        
        self.name = personality["name"]
        self.personality = personality["personality"]
        
        
    def load_system_prompt(self, system_prompt_path: str):
        if (os.path.exists(system_prompt_path) is False):
            raise FileNotFoundError("Cannot find system prompt file")
        
        with open(system_prompt_path, 'r') as file:
            system_prompt = json.load(file)
        if "prompt" not in system_prompt:
            raise IndexError("Prompt is not exists in personality file")
        
        self.system_prompt = system_prompt["prompt"]
        
        
    def message(self, message: LLMMessageList):
        raise NotImplementedError("LLM message function not implemented")