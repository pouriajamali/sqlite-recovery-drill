"""Execute an extracted, hash-checked package with only new synthetic inputs."""
import argparse,hashlib,json,os,subprocess
from pathlib import Path
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def main():
 p=argparse.ArgumentParser();p.add_argument('--package',type=Path,required=True);p.add_argument('--work',type=Path,required=True);p.add_argument('--commit',required=True);p.add_argument('--local-candidate',action='store_true');a=p.parse_args()
 package=a.package.resolve();work=a.work.resolve();work.mkdir(parents=True,exist_ok=False)
 m=json.loads((package/'BUILD-MANIFEST.json').read_text());assert m['sourceCommit']==a.commit and m['selfContained']
 assert m['rid']==('win-x64' if os.name=='nt' else 'linux-x64')
 if not a.local_candidate:assert not m['localCandidate'] and m['originalLicense']=='present'
 expected={f['path'] for f in m['files']}|{'BUILD-MANIFEST.json'};actual={f.relative_to(package).as_posix() for f in package.rglob('*') if f.is_file()};assert actual==expected
 for item in m['files']:
  f=package/item['path'];assert not f.is_symlink() and f.stat().st_size==item['bytes'] and sha(f)==item['sha256']
 assert a.commit.encode() in (package/'drill.dll').read_bytes()
 config=json.loads((package/'drill.runtimeconfig.json').read_text())['runtimeOptions'];assert 'includedFrameworks' in config and 'frameworks' not in config
 exe=package/('drill.exe' if os.name=='nt' else 'drill');assert exe.is_file()
 if os.name=='nt':assert exe.read_bytes()[:2]==b'MZ'
 else:assert exe.read_bytes()[:4]==b'\x7fELF'
 env=dict(os.environ,DOTNET_ROOT=str(work/'no-installed-dotnet'),DOTNET_ROOT_X64=str(work/'no-installed-dotnet'),DOTNET_MULTILEVEL_LOOKUP='0')
 checks=[]
 def run(name,args,expected_exit):
  r=subprocess.run([str(exe),*map(str,args)],cwd=work,env=env,text=True,capture_output=True,timeout=75)
  (work/(name+'.json')).write_text(json.dumps({'command':[str(exe),*map(str,args)],'exit':r.returncode,'stdout':r.stdout,'stderr':r.stderr},indent=2)+'\n',encoding='utf-8')
  assert r.returncode==expected_exit and not r.stderr,(name,r.returncode,r.stderr)
  checks.append(name);return r
 assert m['version'] in run('version',['--version'],0).stdout
 demo=work/'demo with spaces';assert 'DEMO PASS' in run('demo',['demo',demo],0).stdout
 for name,code in [('valid',0),('foreign-key',2),('null-value',2),('batch-count',2),('stale-valid',0)]:
  report=demo/(name+' report');r=json.loads((report/'summary.json').read_text());assert r['exitCode']==code and r['complete'] and r['freshness']==r['externalCompleteness']=='NOT_CHECKED'
  html=(report/'report.html').read_text();assert '<!doctype html>' in html and "default-src 'none'" in html and '<script' not in html and 'https://' not in html
  assert str(work) not in json.dumps(r) and not (report/'trial.sqlite').exists();checks.append('reports-'+name)
 source=demo/'valid.sqlite';before=sha(source)
 r=run('single-check',['check',source,work/'single report','--profile','synthetic-jobs-v1'],0);assert json.loads(r.stdout)['outcome']=='pass' and sha(source)==before
 existing=work/'existing';existing.mkdir();(existing/'sentinel').write_text('untouched')
 r=run('refuse-existing',['check',source,existing,'--profile','synthetic-jobs-v1'],1);assert json.loads(r.stdout)['reason']=='OUTPUT_EXISTS' and (existing/'sentinel').read_text()=='untouched'
 (work/'not sqlite').write_bytes(b'synthetic invalid input')
 r=run('bad-header',['check',work/'not sqlite',work/'bad report','--profile','synthetic-jobs-v1'],1);assert not json.loads(r.stdout)['complete']
 side=Path(str(source)+'-wal');side.write_bytes(b'synthetic sidecar')
 r=run('sidecar',['check',source,work/'side report','--profile','synthetic-jobs-v1'],1);assert json.loads(r.stdout)['reason']=='SIDECAR_UNSUPPORTED' and side.read_bytes()==b'synthetic sidecar' and sha(source)==before
 run('no-user-sql',['--sql','not-executed'],1)
 result={'status':'EXTRACTED_SELF_CONTAINED_PACKAGE_PASS','commit':a.commit,'rid':m['rid'],'checks':checks,'checkCount':len(checks),'manifestSha256':sha(package/'BUILD-MANIFEST.json'),'scope':'Actual apphost and owned children, temporary synthetic files. Bundled runtime/config checked; existing host may have SDKs. No SDK-free OS or rendered-browser proof.'}
 (work/'receipt.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8');print(json.dumps({'status':result['status'],'checks':len(checks),'rid':m['rid']}))
if __name__=='__main__':main()
