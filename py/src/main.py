from api.openai_wrapper import GptModel
from core import Arisu

def main1():
    model = GptModel()
    model.request("Say 'this is a test code' twice using two line")


def main2():
    model = Arisu(personality_path="./param/personality.json", system_prompt_path="./param/system_prompt.json")
    model.run()


if __name__ == "__main__":
    # main1()
    main2()