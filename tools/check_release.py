"""Check exact already-tested runtime archives; source packaging only, no binary rebuild."""
import argparse,hashlib,json,subprocess,tarfile,zipfile
from pathlib import Path,PurePosixPath
ROOT=Path(__file__).resolve().parents[1]
def main():
 p=argparse.ArgumentParser();p.add_argument('--directory',type=Path,required=True);p.add_argument('--commit',required=True);p.add_argument('--tag',required=True);a=p.parse_args()
 assert (ROOT/'LICENSE').is_file(),'Valid original-source license required'
 expected={f'sqlite-recovery-drill-{a.tag}-win-x64.zip',f'sqlite-recovery-drill-{a.tag}-linux-x64.tar.gz'}
 assert {p.name for p in a.directory.iterdir()}==expected
 trees=set();receipts=[]
 for name in sorted(expected):
  path=a.directory/name;payload={}
  if name.endswith('.zip'):
   with zipfile.ZipFile(path) as z:
    for info in z.infolist():
     if not info.is_dir():assert info.filename not in payload and info.file_size<64*1024*1024;payload[info.filename]=z.read(info)
  else:
   with tarfile.open(path,'r:gz') as t:
    for info in t:
     assert info.isfile(),'Only regular archive entries expected'
     assert info.name not in payload and info.size<64*1024*1024;payload[info.name]=t.extractfile(info).read()
  assert len(payload)<=512 and sum(map(len,payload.values()))<256*1024*1024
  assert all(not PurePosixPath(n).is_absolute() and '..' not in PurePosixPath(n).parts for n in payload)
  roots={PurePosixPath(n).parts[0] for n in payload};assert len(roots)==1;prefix=roots.pop()+'/'
  files={n[len(prefix):]:v for n,v in payload.items()};m=json.loads(files.pop('BUILD-MANIFEST.json'))
  assert m['sourceCommit']==a.commit and m['version']==a.tag.removeprefix('v') and m['selfContained'] and not m['localCandidate'] and m['originalLicense']=='present'
  assert set(files)=={v['path'] for v in m['files']} and 'LICENSE' in files
  for item in m['files']:assert len(files[item['path']])==item['bytes'] and hashlib.sha256(files[item['path']]).hexdigest()==item['sha256']
  assert a.commit.encode() in files['drill.dll'];trees.add(m['sourceTree'])
  receipts.append({'archive':name,'rid':m['rid'],'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'bytes':path.stat().st_size})
 assert len(trees)==1
 source=a.directory/f'sqlite-recovery-drill-{a.tag}-source.zip';assert not source.exists()
 subprocess.run(['git','-C',str(ROOT),'-c','core.autocrlf=false','-c','core.eol=lf','archive','--format=zip',f'--prefix=sqlite-recovery-drill-{a.tag}-source/','--output='+str(source.resolve()),a.commit],check=True)
 with zipfile.ZipFile(source) as z:
  names=subprocess.check_output(['git','-C',str(ROOT),'ls-tree','-r','--name-only',a.commit],text=True).splitlines();prefix=f'sqlite-recovery-drill-{a.tag}-source/'
  assert {i.filename for i in z.infolist() if not i.is_dir()}=={prefix+n for n in names}
  for n in names:assert z.read(prefix+n)==subprocess.check_output(['git','-C',str(ROOT),'show',a.commit+':'+n])
 sums=''.join(hashlib.sha256(p.read_bytes()).hexdigest()+'  '+p.name+'\n' for p in sorted(a.directory.iterdir()) if p.suffix in {'.zip','.gz'})
 (a.directory/'SHA256SUMS.txt').write_text(sums,encoding='utf-8',newline='\n')
 print(json.dumps({'status':'ALREADY_TESTED_RUNTIME_ARCHIVES_AND_EXACT_SOURCE_VERIFIED','archives':receipts,'sourceFiles':len(names)}))
if __name__=='__main__':main()
