"""Static checks only; XML parsing does not compile C#/XAML or run .NET tests."""
from pathlib import Path
import hashlib, json, re, subprocess, shutil, xml.etree.ElementTree as ET
root=Path(__file__).resolve().parent.parent
checks=[];errors=[]
def run(name,fn):
 try: detail=fn();checks.append({'name':name,'status':'passed','detail':detail})
 except Exception as exc:errors.append({'name':name,'status':'failed','error':str(exc)})
def xml_check():
 files=[f for f in root.rglob('*') if f.is_file() and 'vendor' not in f.parts and f.suffix in {'.csproj','.props','.xaml','.slnx','.manifest'}]
 for f in files:ET.parse(f)
 return {'files':len(files)}
def reference_check():
 count=0
 for f in root.rglob('*.csproj'):
  for item in ET.parse(f).iter('ProjectReference'):
   target=f.parent/item.attrib['Include'].replace('\\','/')
   assert target.exists(),str(target);count+=1
 for f in root.rglob('*.slnx'):
  for item in ET.parse(f).iter('Project'):
   assert (f.parent/item.attrib['Path'].replace('\\','/')).exists(),str(f)+' '+str(item.attrib)
 return {'projectReferences':count}
def json_check():
 count=0
 for f in root.rglob('*.json'):
  if 'vendor' in f.parts:continue
  json.loads(f.read_text());count+=1
 return {'files':count}
def syntax_check():
 commands=[['node','--check',str(root/'jarvis-mcp-server/src/Jarvis.McpServer/wwwroot/app.js')]]
 commands += [['node','--check',str(f)] for f in (root/'jarvis-agent/src/Jarvis.Agent.Windows/Assets/Browser').rglob('*.js')]
 commands += [['bash','-n',str(f)] for f in (root/'deploy').glob('*.sh')]
 for cmd in commands:subprocess.run(cmd,check=True,capture_output=True,text=True)
 return {'commands':len(commands)}
def baseline_check():
 source=json.loads((root/'docs/baseline-manifest.json').read_text())['files'];omit=set(json.loads((root/'docs/omitted-font-assets.json').read_text())['files'])
 changed=[];same=[];missing=[]
 for item in source:
  file=root/'vendor/jarvis-code'/item['path']
  if not file.exists():missing.append(item['path'])
  elif hashlib.sha256(file.read_bytes()).hexdigest()!=item['sha256']:changed.append(item['path'])
  else:same.append(item['path'])
 assert set(missing)==omit,{'unexpectedMissing':list(set(missing)-omit),'unexpectedPresent':list(omit-set(missing))}
 assert set(changed)=={'CLAUDE.md','Directory.Build.props'},changed
 report={'baselineFiles':len(source),'unchangedFiles':len(same),'intentionalModified':changed,'omittedOptionalFonts':len(missing),'csharpSourcesUnchanged':sum(x.endswith('.cs') for x in same)}
 (root/'docs/baseline-verification.json').write_text(json.dumps(report,indent=2))
 return report
def secret_check():
 banned={'.ttf','.otf','.woff','.woff2','.ttc','.eot','.pfx','.p12','.key','.pem'}
 for f in root.rglob('*'):
  if not f.is_file():continue
  assert f.suffix.lower() not in banned,str(f)
  assert '.private.' not in f.name and f.name!='agent.local.json',str(f)
  data=f.read_bytes()
  if f.name in {'Export-Source.py','Verify-Source.py'}:continue
  assert (b'-----BEGIN OPENSSH ' + b'PRIVATE KEY-----') not in data,str(f)
  assert (b'-----BEGIN RSA ' + b'PRIVATE KEY-----') not in data,str(f)
 return 'No private key/certificate/runtime profile or font binaries in source tree.'
def version_check():
 for file in [root/'Directory.Build.props',root/'vendor/jarvis-code/Directory.Build.props']:
  tree=ET.parse(file);assert tree.find('.//Version').text=='1.0.16';assert tree.find('.//AssemblyVersion').text=='1.0.16.0';assert tree.find('.//FileVersion').text=='1.0.16.0'
 return '1.0.16 / 1.0.16.0'
for name,fn in [('XML well-formedness',xml_check),('Project and solution references',reference_check),('New JSON parse',json_check),('JavaScript and Bash syntax',syntax_check),('Baseline source SHA-256 parity',baseline_check),('Secret and font exclusion',secret_check),('Assembly version bump',version_check)]:run(name,fn)
report={'date':'2026-09-14','kind':'static checks, not compilation','passed':len(checks),'failed':len(errors),'checks':checks+errors,'dotnetAvailable':bool(shutil.which('dotnet'))}
(root/'docs/static-test-results.json').write_text(json.dumps(report,indent=2));print(json.dumps(report,indent=2));raise SystemExit(bool(errors))
