from api.openai_wrapper import LLMModel

if __name__ == "__main__":
    model = LLMModel()
    model.request("How can I resolve when our cat's are fighting?")