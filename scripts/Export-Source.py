"""Export source only, excluding runtime data, secrets, build outputs and font binaries."""
from pathlib import Path
import hashlib, zipfile, sys, re
root=Path(__file__).resolve().parent.parent
version=(root/'VERSION').read_text(encoding='utf-8-sig').strip()
if not re.fullmatch(r'\d+\.\d+\.\d+', version): raise SystemExit('Invalid VERSION')
output=Path(sys.argv[1]).resolve() if len(sys.argv)>1 else root.parent/f'jarvis-platform-{version}-source.zip'
output.parent.mkdir(parents=True, exist_ok=True)
blocked={'.git','.vs','bin','obj','node_modules','artifacts','data','__pycache__','out','testresults','test-results','.pytest_cache','backups'}
extensions={'.pfx','.p12','.key','.pem','.ttf','.otf','.woff','.woff2','.eot','.ttc'}
with zipfile.ZipFile(output,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as archive:
 for path in sorted(root.rglob('*')):
  if path.is_symlink() or not path.is_file() or path==output: continue
  relative=path.relative_to(root)
  if any(part.lower() in blocked for part in relative.parts) or path.suffix.lower() in extensions or '.private.' in path.name or path.name.lower() in {'.env','agent.local.json'} or path.name.lower().endswith(('.db','.db-wal','.db-shm','.sqlite','.sqlite3')): continue
  if (b'-----BEGIN OPENSSH ' + b'PRIVATE KEY-----') in path.read_bytes() or (b'-----BEGIN RSA ' + b'PRIVATE KEY-----') in path.read_bytes(): raise SystemExit('Refusing private-key content: '+str(relative))
  archive.write(path,Path(root.name)/relative)
digest=hashlib.sha256(output.read_bytes()).hexdigest()
output.with_suffix(output.suffix+'.sha256').write_text(digest+'  '+output.name+'\n')
print(str(output));print(digest)
