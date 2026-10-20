import json, socket
s=socket.create_connection(("127.0.0.1",9090))
def send(o): s.sendall((json.dumps(o)+"\n").encode())
send({"type":"auth","apiKey":"YOUR_KEY","client":"python-test"})
send({"v":"1.0","id":"tcp-1","type":"command","module":"playout","channelId":"KTX-PLAYOUT-01","action":"next","params":{}})
print(s.recv(8192).decode())
