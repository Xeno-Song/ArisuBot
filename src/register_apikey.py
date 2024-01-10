from api import api_key_manager as ApiKeyManager

if __name__ == "__main__":
    print("========================================\n")
    print("API Key Registeration\n")
    print("Enter your OpenAI API Key\n")
    print("========================================\n")
    
    print("apikey:", end="")
    api_key = input()
    
    ApiKeyManager.save_apikey(api_key)
    
    print("API key registered")
    
