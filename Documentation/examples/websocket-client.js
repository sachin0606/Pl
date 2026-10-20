const ws = new WebSocket("ws://127.0.0.1:9080/api/v1/events/ws");
ws.onopen = () => {
  ws.send(JSON.stringify({type:"auth", apiKey:"YOUR_KEY"}));
  ws.send(JSON.stringify({type:"subscribe", topics:["playout.KTX-PLAYOUT-01.*","alarm.*"]}));
};
ws.onmessage = e => console.log(JSON.parse(e.data));
