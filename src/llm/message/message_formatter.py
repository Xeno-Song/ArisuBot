
class LLMMessage:
    def __init__(self, role: str = "", message: str = ""):
        self.role = role
        self.message = message


class LLMMessageList:
    def __init__(self):
        self.messages = []
        
    def push(self, message: LLMMessage):
        self.messages.append(message)
        

    def get_messages(self) -> list[LLMMessage]:
        return self.messages