"""Exercise production catalog HTML/JS/CSS in an isolated Chromium profile with explicit API fixtures.
No real account, production token, tool publishing, browser session or computer action is used.
Requires an installed Chrome/Edge and Python websockets (no downloads).
"""
import argparse
import base64
import copy
import importlib.util
import json
import subprocess
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit
from urllib.request import urlopen
from websockets.sync.client import connect

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / 'jarvis-mcp-server/src/Jarvis.McpServer/wwwroot'
spec = importlib.util.spec_from_file_location('consent_cdp', ROOT / 'scripts/Verify-ConsentNavigation.py')
cdp_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cdp_module)

def verify(browser, output):
    fixture = {'role': 'admin', 'fail': False, 'requests': []}
    modes = ['Auto', 'Auto', 'Hidden', 'Hidden', 'Published', 'Published', 'Auto', 'Published']
    fixture['tools'] = [{'id': f'tool-{i}', 'revision': f'r-{i}', 'name': f'{category}__fixture_{i}',
        'agentToolId': f'{category}.fixture_{i}', 'category': category, 'description': f'Fixture {category} capability {i}',
        'publicationMode': modes[i], 'enabled': modes[i] != 'Hidden'}
        for i, category in enumerate(['filesystem', 'filesystem', 'computer', 'computer', 'process', 'process', 'browser', 'visualize'])]
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_): pass
        def reply(self, status, payload, mime='application/json'):
            raw = json.dumps(payload).encode() if mime == 'application/json' else payload
            self.send_response(status); self.send_header('Content-Type', mime)
            self.send_header('Content-Security-Policy', "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'")
            self.end_headers(); self.wfile.write(raw)
        def do_GET(self):
            path = urlsplit(self.path).path
            routes = {
                '/api/auth/csrf': {'token': 'fixture-only'},
                '/api/auth/session': {'user': {'id': 'fixture-admin', 'displayName': 'Test Operator', 'role': fixture['role']}, 'registrationEnabled': False},
                '/api/overview': {'devices': 1, 'online': 1, 'tools': 0, 'callsToday': 0},
                '/api/devices': [{'id': 'fixture-device'}], '/api/activity': [],
                '/api/admin/tools': fixture['tools'], '/api/admin/capabilities': fixture['tools'],
                '/api/devices/fixture-device/tools': fixture['tools']}
            if path in routes: return self.reply(200, copy.deepcopy(routes[path]))
            assets = {'/': ('index.html', 'text/html; charset=utf-8'), '/app.js': ('app.js', 'application/javascript; charset=utf-8'), '/styles.css': ('styles.css', 'text/css; charset=utf-8'), '/theme.css': ('theme.css', 'text/css; charset=utf-8')}
            if path in assets:
                file, mime = assets[path]; return self.reply(200, (WEB / file).read_bytes(), mime)
            self.reply(404, {'error': 'No fixture route'})
        def do_POST(self):
            body = json.loads(self.rfile.read(int(self.headers.get('Content-Length', 0))))
            if self.path != '/api/admin/tools/bulk-availability': return self.reply(404, {})
            fixture['requests'].append(body)
            if fixture['fail']: return self.reply(409, {'error': 'A selected tool changed. Refresh the catalog.'})
            if self.headers.get('X-CSRF-TOKEN') != 'fixture-only': return self.reply(400, {'error': 'Missing CSRF'})
            by_id = {t['id']: t for t in fixture['tools']}
            if any(by_id[t['id']]['revision'] != t['revision'] for t in body['tools']):
                return self.reply(409, {'error': 'Fixture revision conflict'})
            changed = 0
            for row in body['tools']:
                tool = by_id[row['id']]
                if tool['publicationMode'] != body['publicationMode']:
                    changed += 1
                    tool['publicationMode'] = body['publicationMode']
                    tool['enabled'] = body['publicationMode'] != 'Hidden'
                    tool['revision'] += '-next'
            self.reply(200, {'updated': changed, 'selected': len(body['tools']), 'publicationMode': body['publicationMode']})
    server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    origin = f'http://127.0.0.1:{server.server_port}'
    output.parent.mkdir(parents=True, exist_ok=True)
    checks = []
    try:
        with tempfile.TemporaryDirectory(prefix='jarvis-bulk-ui-', dir=output.parent) as profile:
            process = subprocess.Popen([str(browser), '--headless=new', '--no-first-run', '--no-default-browser-check',
                '--remote-debugging-address=127.0.0.1', '--remote-debugging-port=0', '--user-data-dir=' + profile, 'about:blank'],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            try:
                portfile = Path(profile) / 'DevToolsActivePort'; deadline = time.monotonic() + 15
                while not portfile.exists():
                    if process.poll() is not None or time.monotonic() > deadline: raise RuntimeError('Browser did not start')
                    time.sleep(.1)
                port = portfile.read_text().splitlines()[0]
                with urlopen(f'http://127.0.0.1:{port}/json/list', timeout=5) as response: targets = json.load(response)
                target = next(t for t in targets if t['type'] == 'page')
                with connect(target['webSocketDebuggerUrl'], open_timeout=5, max_size=8*1024*1024) as ws:
                    cdp = cdp_module.Cdp(ws)
                    cdp.call('Page.enable'); cdp.call('Runtime.enable'); cdp.call('Log.enable')
                    cdp.call('Emulation.setDeviceMetricsOverride', {'width': 1440, 'height': 1050, 'deviceScaleFactor': 1, 'mobile': False})
                    def wait(expression):
                        deadline = time.monotonic() + 10
                        while not cdp.evaluate(expression):
                            if time.monotonic() > deadline: raise AssertionError('Timeout: ' + expression)
                            time.sleep(.05)
                    def click(selector): cdp.evaluate('document.querySelector(' + json.dumps(selector) + ').click()')
                    def check(name, expression):
                        assert cdp.evaluate(expression), name
                        checks.append({'check': name, 'result': 'PASS'})
                    def search(value):
                        cdp.evaluate("(()=>{const i=document.querySelector('#tool-search');i.value=" + json.dumps(value) + ";i.dispatchEvent(new Event('input',{bubbles:true}));})()")
                    def policy(value):
                        cdp.evaluate("(()=>{const i=document.querySelector('#tool-policy-filter');i.value=" + json.dumps(value) + ";i.dispatchEvent(new Event('change',{bubbles:true}));})()")
                    def screenshot(name):
                        time.sleep(.5) # Capture after the real page-enter animation settles.
                        data = cdp.call('Page.captureScreenshot', {'format': 'png'})['data']
                        output.with_name(name).write_bytes(base64.b64decode(data))
                    cdp.call('Page.navigate', {'url': origin}); wait("!!document.querySelector('[data-page=tools]')")
                    click('[data-page=tools]'); wait("!!document.querySelector('#select-all-tools')")
                    check('availability filter rendered', "document.querySelector('#tool-policy-filter').value==='All' && document.querySelectorAll('[data-tool-select]').length===8")
                    policy('Hidden')
                    check('hidden filter shows only hidden tools', "document.querySelectorAll('[data-tool-select]').length===2 && document.querySelectorAll('#tool-cards .pill.disabled').length===2")
                    policy('Published')
                    check('published filter shows only published tools', "document.querySelectorAll('[data-tool-select]').length===3 && document.querySelectorAll('#tool-cards .pill.active').length===3")
                    policy('Auto')
                    check('auto filter shows only automatic tools', "document.querySelectorAll('[data-tool-select]').length===3 && document.querySelectorAll('#tool-cards .pill.installed').length===3")
                    policy('All')
                    check('initial empty selection', "document.querySelector('[data-action=bulk-publish-tools]').disabled && document.querySelectorAll('[data-tool-select]').length===8")
                    click('#select-all-tools')
                    check('select all eight', "document.querySelectorAll('[data-tool-select]:checked').length===8 && document.querySelector('#select-all-tools').checked")
                    click('[data-tool-select="tool-0"]')
                    check('partial selection mixed state', "document.querySelectorAll('[data-tool-select]:checked').length===7 && document.querySelector('#select-all-tools').indeterminate")
                    click('[data-action=clear-tool-selection]')
                    check('clear disables bulk actions', "document.querySelectorAll('[data-tool-select]:checked').length===0 && document.querySelector('[data-action=bulk-hide-tools]').disabled")
                    search('filesystem'); click('#select-all-tools')
                    check('select all respects search', "document.querySelectorAll('[data-tool-select]:checked').length===2 && document.querySelector('#select-all-label').textContent==='Select all filtered'")
                    screenshot('bulk-selection-desktop.png')
                    click('[data-action=bulk-publish-tools]'); wait('document.querySelector("#modal").open')
                    check('confirmation before mutation', "document.querySelector('[data-action=confirm-tool-availability]').textContent==='Publish 2 tools'")
                    assert not fixture['requests']
                    click('#modal-content [data-action=close-modal]')
                    assert not fixture['requests']; checks.append({'check': 'cancel makes no request', 'result': 'PASS'})
                    click('[data-action=bulk-publish-tools]'); click('[data-action=confirm-tool-availability]')
                    wait("!document.querySelector('#modal').open && document.querySelectorAll('#tool-cards .pill.active').length===2")
                    assert len(fixture['requests']) == 1 and {t['id'] for t in fixture['requests'][0]['tools']} == {'tool-0','tool-1'}
                    check('one atomic publish request and cleared selection', "document.querySelectorAll('[data-tool-select]:checked').length===0 && document.querySelectorAll('.tool-card .pill.active').length===2")
                    click('#select-all-tools'); click('[data-action=bulk-hide-tools]'); click('[data-action=confirm-tool-availability]')
                    wait("!document.querySelector('#modal').open && document.querySelectorAll('#tool-cards .pill.active').length===0")
                    assert len(fixture['requests']) == 2 and fixture['requests'][-1]['publicationMode'] == 'Hidden'
                    checks.append({'check': 'bulk hide uses fresh revisions', 'result': 'PASS'})
                    click('#select-all-tools'); search('computer')
                    check('hidden selections removed on filter change', "document.querySelectorAll('[data-tool-select]:checked').length===0 && document.querySelectorAll('[data-tool-select]:checked').length===0")
                    fixture['fail'] = True
                    click('#select-all-tools'); click('[data-action=bulk-publish-tools]'); click('[data-action=confirm-tool-availability]')
                    wait("!!document.querySelector('#bulk-tool-error')?.textContent && !document.querySelector('[data-action=confirm-tool-availability]').disabled")
                    check('failed batch preserves selection and displays error', "document.querySelector('#modal').open && document.querySelectorAll('[data-tool-select]:checked').length===2 && document.querySelector('#bulk-tool-error').textContent.includes('changed')")
                    fixture['fail'] = False; click('#modal-content [data-action=close-modal]')
                    search('no-match'); check('empty search disables select all', "document.querySelector('#select-all-tools').disabled && document.querySelectorAll('[data-tool-select]:checked').length===0")
                    search(''); click('#select-all-tools')
                    cdp.call('Emulation.setDeviceMetricsOverride', {'width': 390, 'height': 844, 'deviceScaleFactor': 1, 'mobile': True})
                    check('mobile toolbar does not overflow', 'document.documentElement.scrollWidth<=window.innerWidth+1')
                    screenshot('bulk-selection-mobile.png')
                    fixture['role'] = 'user'; cdp.call('Page.navigate', {'url': origin})
                    wait("!!document.querySelector('[data-page=tools]')"); click('[data-page=tools]'); wait("!!document.querySelector('#tool-search')")
                    check('non-admin has no bulk controls or policy filter', "!document.querySelector('#select-all-tools') && !document.querySelector('[data-tool-select]') && !document.querySelector('[data-action=bulk-publish-tools]') && !document.querySelector('#tool-policy-filter')")
                    errors = [e for e in cdp.events if e.get('method') == 'Runtime.exceptionThrown']
                    assert not errors, errors
                    checks.append({'check': 'no uncaught browser exceptions', 'result': 'PASS'})
                    cdp.call('Browser.close')
            finally:
                try: process.wait(timeout=8)
                except subprocess.TimeoutExpired: process.terminate(); process.wait(timeout=5)
    finally:
        server.shutdown(); server.server_close()
    output.write_text(json.dumps({'browser': str(browser), 'scope': 'Real catalog UI with isolated synthetic API fixtures', 'checks': checks}, indent=2), encoding='utf-8')
    print(json.dumps(checks, indent=2))

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--browser', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args(); verify(args.browser, args.output)
