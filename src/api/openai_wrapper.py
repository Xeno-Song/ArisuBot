from api import api_key_manager as ApiKeyManager
from openai import OpenAI

class GptModel:
    def __init__(self):
        self.client = OpenAI(
            api_key = ApiKeyManager.get_apikey()
        )
    
    def request(self, message: str):
        response  = self.client.chat.completions.create(
            messages=[
                {
                    "role": "user",
                    "content": message,
                }
            ],
            model="gpt-3.5-turbo",
            # stream=True
        )
        
        print(response)
        
        # for chunk in stream:
        #     print(chunk.choices[0].delta.content or "", end="")

