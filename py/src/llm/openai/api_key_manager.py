import os
from pathlib import Path

def apikey_path() -> str:
    path = Path(os.path.abspath(__file__))
    project_root_path = path.parent.parent.parent.parent.absolute()
    api_key_conf_path = str(project_root_path) + "\\conf\\api_key.conf"
    return api_key_conf_path


def apikey_exists() -> bool:
    conf_path = apikey_path()
    return os.path.exists(conf_path)
    

def get_apikey() -> str:
    if (apikey_exists() is False):
        return ""
    
    file = open(apikey_path(), 'r')
    key = file.readline()
    file.close()
    
    return key


def save_apikey(apikey:str):
    conf_path = apikey_path()
    
    if (apikey_exists() is True):
        os.remove(conf_path)
    
    file = open(conf_path, 'w')
    file.write(apikey)
    file.close()
