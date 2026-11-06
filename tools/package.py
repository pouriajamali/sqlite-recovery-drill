"""Exact clean-commit self-contained bundles; no publication or rebuilding in aggregation."""
import argparse,hashlib,json,os,platform,shutil,subprocess,tarfile,xml.etree.ElementTree as ET,zipfile
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def git(*args):return subprocess.check_output(['git','-C',str(ROOT),*args],text=True).strip()
def main():
 p=argparse.ArgumentParser();p.add_argument('--rid',choices=['win-x64','linux-x64'],required=True);p.add_argument('--commit',required=True);p.add_argument('--work',type=Path,required=True);p.add_argument('--out',type=Path,required=True);p.add_argument('--dotnet',default='dotnet');p.add_argument('--source');p.add_argument('--local-candidate',action='store_true');a=p.parse_args()
 assert git('rev-parse','HEAD')==a.commit and not git('status','--porcelain'),'Clean exact source required'
 assert platform.machine().lower() in {'amd64','x86_64'}
 if not a.local_candidate:assert a.rid==('win-x64' if os.name=='nt' else 'linux-x64'),'Public releases require matching build OS'
 version=ET.parse(ROOT/'Directory.Build.props').findtext('.//Version');assert version
 if not a.local_candidate:assert (ROOT/'LICENSE').is_file(),'Explicit original-source license required before distribution'
 work=a.work.resolve();work.mkdir(parents=True,exist_ok=False);a.out.mkdir(parents=True,exist_ok=True)
 published=work/'published';command=[a.dotnet,'publish','src/RecoveryDrill/RecoveryDrill.csproj','-c','Release','-r',a.rid,'--self-contained','true','-p:RuntimeFrameworkVersion=10.0.14','-p:PublishTrimmed=false','-p:PublishAot=false','-p:PublishReadyToRun=false','-p:DebugType=None','-p:DebugSymbols=false','-p:SourceRevisionId='+a.commit,'-o',str(published)]
 if a.source:command+=['--source',a.source,'-p:NuGetAudit=false']
 with (work/'publish.log').open('wb') as log:subprocess.run(command,cwd=ROOT,stdout=log,stderr=subprocess.STDOUT,check=True,timeout=240)
 assert not git('status','--porcelain'),'Build modified source'
 name=f'sqlite-recovery-drill-v{version}-{a.rid}';package=work/name;shutil.copytree(published,package)
 assert a.commit.encode() in (package/'drill.dll').read_bytes(),'Source revision missing'
 for f in ['README.md','PROVENANCE.md','SIGNING-PUBLIC-KEY.pub','NOTICE.md']:shutil.copy2(ROOT/f,package/f)
 if (ROOT/'LICENSE').exists():shutil.copy2(ROOT/'LICENSE',package/'LICENSE')
 else:(package/'LICENSE-PENDING.txt').write_text('Private local candidate. Original-source license/public distribution awaits an explicit owner decision. Signing does not grant a license.\n',encoding='utf-8')
 licenses=package/'licenses';licenses.mkdir()
 assets=json.loads((ROOT/'src/RecoveryDrill/obj/project.assets.json').read_text())
 folders=[Path(f) for f in assets['packageFolders']];dependencies=[]
 for identity,entry in sorted(assets['libraries'].items()):
  if entry['type']!='package':continue
  package_id,version_id=identity.split('/')
  directory=next(folder/package_id.lower()/version_id for folder in folders if (folder/package_id.lower()/version_id).is_dir())
  nuspec=ET.parse(next(directory.glob('*.nuspec'))).find('{*}metadata');assert nuspec is not None
  license_id=nuspec.findtext('{*}license');assert license_id in {'MIT','Apache-2.0'},(identity,license_id)
  dependencies.append({'id':package_id,'version':version_id,'license':license_id,'copyright':nuspec.findtext('{*}copyright',default='')})
  for f in directory.iterdir():
   if f.is_file() and (f.name.upper().startswith('LICENSE') or f.name.upper().startswith('THIRD-PARTY-NOTICES')):shutil.copy2(f,licenses/(package_id+'-'+f.name))
 # SDK runtime packs are download dependencies and may be absent from libraries.
 runtime_id='Microsoft.NETCore.App.Runtime.'+a.rid
 runtime_pack=next(folder/runtime_id.lower()/'10.0.14' for folder in folders if (folder/runtime_id.lower()/'10.0.14').is_dir())
 notices=[f for f in runtime_pack.iterdir() if f.is_file() and (f.name.upper().startswith('LICENSE') or f.name.upper().startswith('THIRD-PARTY-NOTICES'))]
 assert any(f.name.upper().startswith('LICENSE') for f in notices) and any(f.name.upper().startswith('THIRD-PARTY-NOTICES') for f in notices),'Runtime redistribution notices required'
 for f in notices:shutil.copy2(f,licenses/(runtime_id+'-'+f.name))
 if runtime_id.lower() not in {d['id'].lower() for d in dependencies}:dependencies.append({'id':runtime_id,'version':'10.0.14','license':'MIT','copyright':'.NET Foundation and contributors; see bundled runtime notices'})
 (package/'DEPENDENCIES.json').write_text(json.dumps({'packages':dependencies,'sqlite':'SQLite core is public domain; SQLitePCLRaw wrappers retain their package notices.','runtime':'Bundled .NET runtime retains Microsoft/.NET Foundation notices.'},indent=2)+'\n',encoding='utf-8')
 config=json.loads((package/'drill.runtimeconfig.json').read_text())['runtimeOptions'];assert 'includedFrameworks' in config and 'frameworks' not in config
 files=[]
 for f in sorted(package.rglob('*')):
  assert not f.is_symlink()
  if not f.is_file():continue
  path=f.relative_to(package).as_posix();assert not path.lower().endswith(('.pdb','.sqlite','.db')) and '.tests' not in path.lower()
  files.append({'path':path,'bytes':f.stat().st_size,'sha256':sha(f)})
 manifest={'schema':1,'version':version,'rid':a.rid,'sourceCommit':a.commit,'sourceTree':git('rev-parse','HEAD^{tree}'),'selfContained':True,'localCandidate':a.local_candidate,'originalLicense':'present' if (package/'LICENSE').exists() else 'PENDING_OWNER_DECISION','buildHost':{'os':platform.system(),'architecture':platform.machine()},'runtimeConfig':config,'files':files}
 (package/'BUILD-MANIFEST.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
 archive=a.out/(name+('.zip' if a.rid=='win-x64' else '.tar.gz'));assert not archive.exists()
 if a.rid=='win-x64':
  with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED) as z:
   for f in sorted(package.rglob('*')):
    if f.is_file():z.write(f,f.relative_to(work).as_posix())
 else:
  with tarfile.open(archive,'x:gz') as t:
   for f in sorted(package.rglob('*')):
    if f.is_file():
     info=t.gettarinfo(str(f),arcname=f.relative_to(work).as_posix());info.mode=0o755 if f.name in {'drill','createdump'} else 0o644;info.uid=info.gid=0;info.uname=info.gname=''
     with f.open('rb') as stream:t.addfile(info,stream)
 result={'archive':str(archive.resolve()),'bytes':archive.stat().st_size,'sha256':sha(archive),'commit':a.commit,'rid':a.rid,'payloadFiles':len(files),'localCandidate':a.local_candidate}
 (work/'archive.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8');print(json.dumps(result))
if __name__=='__main__':main()
