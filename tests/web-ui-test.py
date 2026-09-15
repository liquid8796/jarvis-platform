"""Browser/UI regression with explicit synthetic API fixtures. Does NOT test ASP.NET or live MCP."""
from pathlib import Path
import json, re
from urllib.parse import urlparse
from playwright.sync_api import sync_playwright, expect
ROOT=Path(__file__).resolve().parent.parent
WEB=ROOT/'jarvis-mcp-server/src/Jarvis.McpServer/wwwroot'
OUT=ROOT/'docs/screenshots';OUT.mkdir(parents=True,exist_ok=True)
state={'authenticated':False,'registered':False,'csrf':0}
user={'id':'u1','displayName':'Alex Morgan','email':'alex@example.test','role':'admin','status':'active'}
users=[user,{'id':'u2','displayName':'Jordan Lee','email':'jordan@example.test','role':'user','status':'pending'},{'id':'u3','displayName':'Casey Nguyen','email':'casey@example.test','role':'user','status':'active'}]
devices=[{'id':'d1','name':'Development workstation','enabled':True,'online':True,'platform':'Windows 11 · Desktop','agentVersion':'1.0.16.0','lastSeenAt':1789372800,'toolCount':52,'revision':'r1'},{'id':'d2','name':'Build laptop','enabled':True,'online':False,'platform':'Windows 10 · Laptop','agentVersion':'1.0.16.0','lastSeenAt':1789372000,'toolCount':49,'revision':'r2'}]
categories=[('computer','click','Click or double-click at a position in an approved application.'),('computer','screenshot','Capture an approved desktop application for visual inspection.'),('browser','navigate','Navigate a browser tab through the isolated native bridge.'),('browser','read_page','Read the accessibility structure of the current browser page.'),('filesystem','Read','Read a source file inside the selected workspace.'),('filesystem','Edit','Apply a targeted replacement to a source file.'),('process','start','Start an approved build or test job and receive its job ID.'),('process','read','Read bounded logs and exit status of an owned process.'),('visualize','show_widget','Render an isolated visual artifact locally in Jarvis Agent.')]
capabilities=[dict(id=f'{c}.{n}',name=f'{c}__{n}',category=c,description=d,readOnly=n in ['Read','read','read_page','screenshot'],inputSchema={'type':'object','properties':{'target':{'type':'string'}},'additionalProperties':False}) for c,n,d in categories]
tools=[dict(id=f't{i}',agentToolId=c['id'],revision='r1',enabled=i!=5,**{k:c[k] for k in ['name','category','description']}) for i,c in enumerate(capabilities)]
activity=[dict(id=i,time=1789372800-i*60,userId='u1',action=a,outcome=o,durationMs=ms) for i,(a,o,ms) in enumerate([('tool.process__start','completed',86),('tool.filesystem__Read','completed',34),('tool.computer__screenshot','completed',112),('device.enroll','created',0),('account.login','completed',0)])]
checks=[]
def check(name):
 checks.append({'name':name,'status':'passed'});print('PASS:',name,flush=True)
def handler(path,method,body):
 status=200;data=None;body=body or {}
 if path=='/api/auth/csrf':state['csrf']+=1;data={'token':'fixture-csrf-'+str(state['csrf'])}
 elif path=='/api/auth/session': data={'user':user if state['authenticated'] else None,'registrationEnabled':True,'mcpEndpoint':'https://jarvis.example.test/mcp'}
 elif path=='/api/auth/register': state['registered']=True;data={'message':'Account created. Waiting for administrator approval.'}
 elif path=='/api/auth/login':state['authenticated']=True;data={'user':user}
 elif path in ['/api/auth/logout','/api/auth/revoke']:state['authenticated']=False;status=204
 elif path=='/api/overview':data={'devices':len(devices),'online':sum(d['online'] for d in devices),'tools':len(tools),'callsToday':18,'version':'1.0.16'}
 elif path=='/api/activity':data=activity
 elif path=='/api/devices' and method=='GET':data=devices
 elif path=='/api/devices' and method=='POST':
  ident='d'+str(len(devices)+1);devices.append(dict(id=ident,name=body['name'],enabled=True,online=False,platform='',agentVersion='',lastSeenAt=0,toolCount=0,revision='r1'));data={'deviceId':ident,'serverUrl':'https://jarvis.example.test','token':'jra_'+'0'*64}
 elif path.startswith('/api/devices/'):
  ident=path.split('/')[3];device=next(d for d in devices if d['id']==ident)
  if path.endswith('/rotate'):data={'deviceId':ident,'serverUrl':'https://jarvis.example.test','token':'jra_'+'1'*64}
  elif path.endswith('/tools'):data=capabilities
  elif method=='PUT':device.update(body);status=204
  elif method=='DELETE':devices.remove(device);status=204
 elif path=='/api/admin/capabilities':data=capabilities
 elif path=='/api/admin/tools/import':data={'imported':0}
 elif path=='/api/admin/tools' and method=='GET':data=tools
 elif path=='/api/admin/tools' and method=='POST':
  cap=next(c for c in capabilities if c['id']==body['agentToolId']);entry=dict(body,id='t'+str(len(tools)),category=cap['category'],revision='r1');tools.append(entry);data=entry
 elif path.startswith('/api/admin/tools/'):
  entry=next(t for t in tools if t['id']==path.split('/')[-1])
  if method=='GET':data={'tool':entry,'capability':next(c for c in capabilities if c['id']==entry['agentToolId'])}
  elif method=='PUT':entry.update(body);data=entry
  elif method=='DELETE':tools.remove(entry);status=204
 elif path=='/api/admin/users' and method=='GET':data=users
 elif path=='/api/admin/users' and method=='POST':
  entry=dict(body,id='u'+str(len(users)+1));entry.pop('password',None);users.append(entry);data=entry
 elif path.startswith('/api/admin/users/'):
  entry=next(u for u in users if u['id']==path.split('/')[-1])
  if method=='PUT':entry.update(body);data=entry
  elif method=='DELETE':users.remove(entry);status=204
 else:status=404;data={'error':'Fixture route not implemented: '+path}
 return {'status':status,'data':data}
errors=[]
try:
 with sync_playwright() as pw:
  browser=pw.chromium.launch(executable_path='/usr/bin/chromium',headless=True,args=['--no-sandbox'])
  context=browser.new_context(viewport={'width':1440,'height':1100},device_scale_factor=1,reduced_motion='reduce')
  context.expose_function('fixtureRequest',handler)
  page=context.new_page();page.set_default_timeout(5000);page.on('pageerror',lambda error:errors.append(str(error)));page.on('dialog',lambda d:d.accept())
  # Execute the actual assets entirely in-memory; no network navigation or server access.
  html=(WEB/'index.html').read_text()
  html=re.sub(r'<link[^>]+>', '', html);html=re.sub(r'<script[^>]*>.*?</script>', '', html, flags=re.S)
  page.set_content(html)
  page.add_style_tag(content=(WEB/'styles.css').read_text())
  page.add_script_tag(content='''window.fetch=async (url,options={})=>{const response=await window.fixtureRequest(url,options.method||"GET",options.body?JSON.parse(options.body):null);return new Response(response.status===204?null:JSON.stringify(response.data),{status:response.status,headers:{"Content-Type":"application/json"}})};''')
  page.add_script_tag(content=(WEB/'app.js').read_text())
  expect(page.get_by_role('heading',name='Welcome back.')).to_be_visible()
  page.screenshot(path=str(OUT/'web-login.png'),full_page=True);check('Login page renders')
  page.get_by_role('button',name='Create an account',exact=True).click();page.locator('[name=displayName]').fill('Test Registrant');page.locator('[name=email]').fill('test@example.test');page.locator('[name=password]').fill('Fixture-Password-42!');page.get_by_role('button',name='Create account',exact=False).click();expect(page.get_by_role('heading',name='Welcome back.')).to_be_visible();assert state['registered'];check('Registration submission and pending-approval message')
  page.locator('[name=email]').fill('alex@example.test');page.locator('[name=password]').fill('Fixture-Password-42!');page.get_by_role('button',name='Sign in to workspace').click();expect(page.locator('.metrics')).to_be_visible();check('Login transitions to authenticated dashboard')
  page.wait_for_function("!document.querySelector('#toast').classList.contains('visible')");page.screenshot(path=str(OUT/'web-dashboard.png'),full_page=True)
  assert page.locator('.metric-value').all_text_contents()==['2','1','9','18'];check('Dashboard displays API values, not static metrics')
  page.locator('nav [data-page=devices]').click();page.get_by_role('button',name='Enroll device',exact=False).first.click();page.locator('#modal [name=name]').fill('Test machine');page.get_by_role('button',name='Save changes').click();expect(page.locator('#enrollment-token')).to_be_visible();check('Enrollment shows one-time token dialog')
  page.get_by_role('button',name='I’ve saved it').click();expect(page.get_by_text('Test machine',exact=True)).to_be_visible()
  page.locator('[data-action=edit-device][data-id=d3]').click();page.locator('#modal [name=name]').fill('Renamed machine');page.get_by_role('button',name='Save changes').click();expect(page.get_by_text('Renamed machine',exact=True)).to_be_visible();check('Device edit persists API update')
  page.locator('[data-action=edit-device][data-id=d3]').click();page.get_by_role('button',name='Rotate token').click();expect(page.locator('#enrollment-token')).to_have_value('jra_'+'1'*64);page.get_by_role('button',name='I’ve saved it').click();check('Device token rotation flow')
  page.locator('[data-action=edit-device][data-id=d3]').click();page.get_by_role('button',name='Delete',exact=True).click();expect(page.get_by_text('Renamed machine',exact=True)).to_have_count(0);check('Device deletion flow')
  page.locator('nav [data-page=tools]').click();expect(page.locator('.tool-card')).to_have_count(9);page.wait_for_function("!document.querySelector('#toast').classList.contains('visible')");page.screenshot(path=str(OUT/'web-tools.png'),full_page=True)
  page.get_by_label('Search tools').fill('process');expect(page.locator('.tool-card')).to_have_count(2);check('Tool catalog search')
  page.locator('[data-action=tool-detail]').first.click();expect(page.get_by_role('heading',name='Input schema')).to_be_visible();check('Tool detail shows schema')
  page.get_by_role('button',name='Edit tool',exact=True).click();page.locator('#modal [name=description]').fill('Updated managed build tool');page.get_by_role('button',name='Save changes').click();expect(page.get_by_text('Updated managed build tool',exact=True)).to_be_visible();check('Tool editor saves changes')
  page.get_by_label('Search tools').fill('');page.get_by_role('button',name='Import installed').click();expect(page.locator('#toast')).to_contain_text('0 capabilities');check('Capability import feedback')
  page.locator('nav [data-page=users]').click();expect(page.get_by_role('heading',name='People & permissions.')).to_be_visible();page.locator('[data-action=edit-user][data-id=u2]').click();page.locator('#modal [name=status]').select_option('active');page.get_by_role('button',name='Save changes').click();assert users[1]['status']=='active';check('Administrator approves pending user')
  page.get_by_role('button',name='Add user',exact=False).click();page.locator('#modal [name=displayName]').fill('<img src=x onerror="window.injected=1">');page.locator('#modal [name=email]').fill('safe@example.test');page.locator('#modal [name=password]').fill('Fixture-Password-42!');page.get_by_role('button',name='Save changes').click();expect(page.get_by_text('<img src=x onerror="window.injected=1">',exact=True)).to_be_visible();assert page.evaluate('window.injected') is None;check('Untrusted user label is escaped, not executed')
  page.locator('nav [data-page=activity]').click();expect(page.get_by_role('heading',name='An accountable workspace.')).to_be_visible();check('Audit activity view')
  page.locator('[data-action=security]').click();expect(page.get_by_role('heading',name='You remain in control.')).to_be_visible();page.get_by_role('button',name='Close',exact=True).click();check('Local control and revocation help')
  page.locator('[data-action=connection-help]').click();expect(page.locator('#modal pre')).to_contain_text('https://jarvis.example.test/mcp');page.get_by_role('button',name='Close',exact=True).click();check('OAuth endpoint help')
  page.locator('nav [data-page=overview]').click();page.set_viewport_size({'width':390,'height':844});expect(page.locator('.metrics')).to_be_visible();page.wait_for_function("!document.querySelector('#toast').classList.contains('visible')");page.screenshot(path=str(OUT/'web-mobile.png'),full_page=True)
  overflow=page.evaluate('document.documentElement.scrollWidth > innerWidth');assert not overflow;check('Mobile layout has no document horizontal overflow')
  page.get_by_role('button',name='Open navigation').click();expect(page.locator('.sidebar')).to_have_class('sidebar open');check('Mobile navigation opens')
  page.locator('[data-action=logout]').click();expect(page.get_by_role('heading',name='Welcome back.')).to_be_visible();check('Logout returns to login')
  assert not errors,errors;check('No browser pageerror events')
  browser.close()
finally:
 report={'suite':'Web UI regression — mocked API fixtures, NOT live server/MCP','checks':checks,'passed':len(checks),'pageErrors':errors,'screenshots':'docs/screenshots','date':'2026-09-14'}
 (ROOT/'docs/web-ui-test-results.json').write_text(json.dumps(report,indent=2))
print(json.dumps(report,indent=2))
