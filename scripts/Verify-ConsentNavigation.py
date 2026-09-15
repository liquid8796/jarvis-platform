"""Run Chrome CSP navigation checks with sanitized ASP.NET consent fixtures.
Requires Python and websockets; uses an installed Chrome/Edge, no downloads.
OAuth server validation is covered by xUnit; HTTP redirects here are synthetic.
No production credentials, browser profile, security bypass or real grants used.
"""
import argparse
import json
import subprocess
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlencode, urlsplit
from urllib.request import urlopen
from websockets.sync.client import connect

class Cdp:
    def __init__(self, socket):
        self.socket, self.seq, self.events = socket, 0, []

    def call(self, method, params=None):
        self.seq += 1
        self.socket.send(json.dumps({'id': self.seq, 'method': method, 'params': params or {}}))
        while True:
            reply = json.loads(self.socket.recv(timeout=12))
            if reply.get('id') == self.seq:
                if 'error' in reply: raise RuntimeError(reply['error'])
                return reply.get('result', {})
            self.events.append(reply)

    def evaluate(self, expression):
        result = self.call('Runtime.evaluate', {'expression': expression, 'returnByValue': True})
        if 'exceptionDetails' in result: raise RuntimeError(result['exceptionDetails'])
        return result.get('result', {}).get('value')

def verify(fixture, browser, output):
    state = {'posts': [], 'callbacks': [], 'unexpected': []}
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_): pass
        def do_GET(self):
            if self.server is callback:
                if urlsplit(self.path).path == '/callback':
                    state['callbacks'].append(parse_qs(urlsplit(self.path).query))
                body = '<title>OAuth callback fixture</title>Callback reached.'
            else:
                body = fixture['html'] if self.path.startswith('/?case=') else ''
            self.send_response(200)
            self.send_header('Content-Type', 'text/html; charset=utf-8')
            self.send_header('Content-Security-Policy', state['policy'])
            self.end_headers()
            self.wfile.write(body.encode('utf-8'))
        def do_POST(self):
            data = self.rfile.read(int(self.headers.get('Content-Length', 0))).decode()
            if self.server is callback:
                state['unexpected'].append(self.path)
                self.send_response(400); self.end_headers(); return
            fields = parse_qs(data); state['posts'].append(fields)
            values = {'state': 'synthetic-state'}
            values.update({'code': 'synthetic-code'} if fields.get('decision') == ['allow'] else {'error': 'access_denied'})
            self.send_response(302)
            self.send_header('Location', callback_origin + '/callback?' + urlencode(values))
            self.send_header('Content-Security-Policy', fixture['defaultPolicy'])
            self.end_headers()
    front = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
    callback = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
    origin = f'http://127.0.0.1:{front.server_port}'
    callback_origin = f'http://127.0.0.1:{callback.server_port}'
    for server in (front, callback):
        threading.Thread(target=server.serve_forever, daemon=True).start()
    output.parent.mkdir(parents=True, exist_ok=True)
    results = []
    try:
        with tempfile.TemporaryDirectory(prefix='jarvis-consent-chrome-', dir=output.parent) as profile:
            process = subprocess.Popen([str(browser), '--headless=new', '--no-first-run',
                '--no-default-browser-check', '--remote-debugging-address=127.0.0.1',
                '--remote-debugging-port=0', '--user-data-dir=' + profile, 'about:blank'],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            try:
                portfile = Path(profile) / 'DevToolsActivePort'
                deadline = time.monotonic() + 15
                while not portfile.exists():
                    if process.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError('Isolated Chrome did not start.')
                    time.sleep(.1)
                port = portfile.read_text().splitlines()[0]
                with urlopen(f'http://127.0.0.1:{port}/json/list', timeout=5) as response:
                    targets = json.load(response)
                target = next(t for t in targets if t['type'] == 'page')
                with connect(target['webSocketDebuggerUrl'], open_timeout=5, max_size=4*1024*1024) as ws:
                    cdp = Cdp(ws)
                    cdp.call('Page.enable'); cdp.call('Runtime.enable'); cdp.call('Log.enable')
                    cases = [('baseline-blocks-callback', False, 'allow', False),
                             ('patched-authorize', True, 'allow', False),
                             ('patched-cancel', True, 'deny', False),
                             ('unapproved-origin-blocked', True, 'allow', True)]
                    for name, patched, decision, tamper in cases:
                        state.update(posts=[], callbacks=[], unexpected=[])
                        state['policy'] = (fixture['consentPolicy'].replace(fixture['callbackOrigin'], callback_origin)
                                           if patched else fixture['defaultPolicy'])
                        cdp.events.clear()
                        page = origin + '/?case=' + name
                        cdp.call('Page.navigate', {'url': page})
                        deadline = time.monotonic() + 10
                        while not cdp.evaluate('location.href===' + json.dumps(page) + '&&document.readyState==="complete"'):
                            if time.monotonic() > deadline: raise RuntimeError('Consent page did not load.')
                            time.sleep(.05)
                        if tamper:
                            cdp.evaluate('document.querySelector("form").action=' +
                                json.dumps(f'http://localhost:{callback.server_port}/unapproved'))
                        cdp.evaluate('document.querySelector(' + json.dumps('button[value="' + decision + '"]') + ').click()')
                        deadline = time.monotonic() + 2
                        while time.monotonic() < deadline:
                            time.sleep(.1)
                            cdp.evaluate('document.readyState')
                            if state['callbacks']: break
                        violations = [e for e in cdp.events if e.get('method') == 'Log.entryAdded'
                                      and 'form-action' in e.get('params', {}).get('entry', {}).get('text', '')]
                        passed = (len(state['posts']) == 1 and len(state['callbacks']) == 1 and not violations
                                  if patched and not tamper else
                                  not state['callbacks'] and not state['unexpected'] and bool(violations))
                        if patched and not tamper:
                            returned = state['callbacks'][0]
                            passed = passed and returned.get('state') == ['synthetic-state']
                            passed = passed and returned.get('code' if decision == 'allow' else 'error') == [
                                'synthetic-code' if decision == 'allow' else 'access_denied']
                        results.append({'case': name, 'posts': len(state['posts']),
                            'callbackHits': len(state['callbacks']), 'cspFormActionViolations': len(violations),
                            'result': 'PASS' if passed else 'FAIL'})
                    cdp.call('Browser.close')
            finally:
                try: process.wait(timeout=8)
                except subprocess.TimeoutExpired: process.terminate(); process.wait(timeout=5)
    finally:
        for server in (front, callback): server.shutdown(); server.server_close()
    output.write_text(json.dumps({'browser': str(browser), 'scope': 'Sanitized consent fixtures; Chromium CSP enforcement',
        'checks': results}, indent=2), encoding='utf-8')
    print(json.dumps(results, indent=2))
    if len(results) != 4 or any(row['result'] != 'PASS' for row in results):
        raise SystemExit('Browser consent navigation regression failed.')

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fixture', required=True, type=Path)
    parser.add_argument('--browser', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    verify(json.loads(args.fixture.read_text(encoding='utf-8-sig')), args.browser, args.output)
