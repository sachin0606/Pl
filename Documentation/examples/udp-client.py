import json, socket
msg={"v":"1.0","id":"udp-1","type":"command","module":"cg","channelId":"KTX-PLAYOUT-01","action":"stop","params":{"layer":20}}
sock=socket.socket(socket.AF_INET,socket.SOCK_DGRAM)
sock.sendto(json.dumps(msg).encode(),("127.0.0.1",9091))
