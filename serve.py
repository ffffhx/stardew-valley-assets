from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import json
import os

root = Path(__file__).resolve().parent
os.chdir(root)
server = ThreadingHTTPServer(('127.0.0.1', 0), SimpleHTTPRequestHandler)
state = {'url': f'http://127.0.0.1:{server.server_port}/', 'pid': os.getpid()}
(root / 'logs').mkdir(exist_ok=True)
(root / 'logs' / 'viewer.json').write_text(json.dumps(state), encoding='utf-8')
print(json.dumps(state), flush=True)
server.serve_forever()
