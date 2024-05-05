
class MemoryDatabase():
    def init(self):
        self.storage = {}
        
    def save_message(self, username, message):
        self.storage[username] = message
        
    def read_memory(self, username):
        if username in self.storage:
            return self.storage[username]
        
        return None
    
    def erase_memory(self, username):
        if username in self.storage:
            self.storage[username] = None
            
    
    def reset_memory(self, username):
        self.storage[username] = {}
        
    