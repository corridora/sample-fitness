#!/usr/bin/env python3
import argparse
import getpass
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / '.local'
SECRET_NAMES = {'CORRIDORA_INGEST_KEY', 'CORRIDORA_RUM_KEY', 'DEMO_CONTROL_TOKEN', 'DOWNSTREAM_AUTH_TOKEN'}
DEFAULTS = {
 'SAMPLE_MODE':'local','PORT':'4104','CORRIDORA_TARGET':'own','CORRIDORA_OTLP_ENDPOINT':'https://ingest.corridora.com',
 'CORRIDORA_RUM_SCRIPT_URL':'https://rum.corridora.com/corridora-rum.js','OTEL_SERVICE_NAME':'sample-fitness-api',
 'OTEL_SERVICE_NAMESPACE':'corridora-samples.fitness','DEPLOYMENT_ENVIRONMENT':'development','DEMO_DAILY_START_UTC':'14:00',
 'DEMO_WINDOW_SECONDS':'600','ENABLE_PROVIDER_FORWARDING':'false','ENABLE_SYNTHETICS':'false','RECOMMENDER_URL':'',
 'PUBLIC_URL':'', 'AZURE_SUBSCRIPTION_ID':'','AZURE_LOCATION':'eastus','AZURE_RESOURCE_GROUP':'corridora-fitness','RESOURCE_PREFIX':'fitdemo',
 'EXISTING_RESOURCE_GROUP':'false','AZURE_EXISTING_COSMOS_NAME':'','COSMOS_FREE_TIER':'true','IMAGE_REPOSITORY':'ghcr.io/corridora/sample-fitness',
 'CORRIDORA_INGEST_KEY':'','CORRIDORA_RUM_KEY':'','DEMO_CONTROL_TOKEN':'','DOWNSTREAM_AUTH_TOKEN':''
}

def protected_write(path, value):
 LOCAL.mkdir(parents=True, exist_ok=True)
 old = os.umask(0o077)
 try:
  descriptor, temporary = tempfile.mkstemp(dir=path.parent)
  with os.fdopen(descriptor, 'w') as handle: json.dump(value, handle, indent=2); handle.write('\n')
  os.chmod(temporary, 0o600); os.replace(temporary, path)
 finally: os.umask(old)

def load(path=None):
 cfg = DEFAULTS.copy()
 for source in [Path(path) if path else LOCAL / 'config.json', LOCAL / 'secrets.json']:
  if source.exists():
   if source.name == 'secrets.json' and source.stat().st_mode & 0o077: raise ValueError('Secret file must have owner-only permissions (chmod 600).')
   values = json.loads(source.read_text())
   if any(values.get(k) for k in SECRET_NAMES) and source.stat().st_mode & 0o077:raise ValueError('Configuration containing secrets must have owner-only permissions (chmod 600).')
   cfg.update({str(k):str(v) for k,v in values.items()})
 cfg.update({k:os.environ[k] for k in cfg if k in os.environ})
 return cfg

def validate(cfg):
 if cfg['SAMPLE_MODE'] not in ('local','cloud'): raise ValueError('SAMPLE_MODE must be local or cloud')
 if cfg['CORRIDORA_TARGET'] not in ('own','demo'): raise ValueError('CORRIDORA_TARGET must be own or demo')
 if not 1024 <= int(cfg['PORT']) <= 65535: raise ValueError('PORT must be 1024..65535')
 if not 1 <= int(cfg['DEMO_WINDOW_SECONDS']) <= 600: raise ValueError('DEMO_WINDOW_SECONDS must be 1..600')
 if not re.fullmatch(r'(?:[01]\d|2[0-3]):[0-5]\d',cfg['DEMO_DAILY_START_UTC']): raise ValueError('Daily start must be HH:mm UTC')
 for key in ('ENABLE_SYNTHETICS','ENABLE_PROVIDER_FORWARDING','EXISTING_RESOURCE_GROUP','COSMOS_FREE_TIER'):
  if cfg[key] not in ('true','false'): raise ValueError(key+' must be true or false')
 from urllib.parse import urlparse
 for key in ('CORRIDORA_OTLP_ENDPOINT','CORRIDORA_RUM_SCRIPT_URL','RECOMMENDER_URL'):
  if not cfg[key]: continue
  url=urlparse(cfg[key]); local=url.hostname in ('localhost','127.0.0.1','::1')
  if url.username or url.password or url.fragment or (url.scheme!='https' and not (cfg['SAMPLE_MODE']=='local' and local and url.scheme=='http')): raise ValueError(key+' requires HTTPS without URL credentials (local HTTP permitted)')
 if cfg['CORRIDORA_TARGET']=='demo' or cfg['SAMPLE_MODE']=='cloud':
  for key in ('CORRIDORA_INGEST_KEY','CORRIDORA_RUM_KEY'):
   if not cfg[key]: raise ValueError(key+' requires an operator-provisioned scoped key')
 if not cfg['DEMO_CONTROL_TOKEN']: raise ValueError('DEMO_CONTROL_TOKEN is required; configure generates a local token')
 if '\n' in cfg['CORRIDORA_INGEST_KEY'] or ',' in cfg['CORRIDORA_INGEST_KEY']: raise ValueError('Invalid backend key format')
 if cfg['SAMPLE_MODE']=='cloud':
  for key in ('AZURE_SUBSCRIPTION_ID','AZURE_RESOURCE_GROUP','RESOURCE_PREFIX','IMAGE_REPOSITORY'):
   if not cfg[key]: raise ValueError(key+' required for Azure deployment')
  if not re.fullmatch(r'[a-z][a-z0-9-]{2,11}',cfg['RESOURCE_PREFIX']): raise ValueError('RESOURCE_PREFIX needs 3..12 lowercase letters/digits/hyphens, beginning with a letter')
  if not re.fullmatch(r'ghcr\.io/[a-z0-9._/-]+',cfg['IMAGE_REPOSITORY']): raise ValueError('IMAGE_REPOSITORY must be a public GHCR repository without tag; existing docker login required')
  if not cfg['AZURE_EXISTING_COSMOS_NAME'] and cfg['COSMOS_FREE_TIER']!='true': raise ValueError('Budget baseline requires an eligible free-tier Cosmos account or explicitly chosen existing free-tier account')

def configure(args):
 cfg=load(args.config)
 prompts=[('SAMPLE_MODE','Run locally or in Azure (local/cloud)'),('CORRIDORA_TARGET','Corridora destination (own/demo)'),('CORRIDORA_OTLP_ENDPOINT','Corridora ingest base URL'),('CORRIDORA_INGEST_KEY','Backend ingest key (blank disables local export)'),('CORRIDORA_RUM_KEY','Browser RUM key (blank disables local RUM)'),('CORRIDORA_RUM_SCRIPT_URL','RUM script URL'),('OTEL_SERVICE_NAME','API service name'),('OTEL_SERVICE_NAMESPACE','Service namespace'),('DEPLOYMENT_ENVIRONMENT','Environment'),('PORT','Local HTTP port'),('DEMO_DAILY_START_UTC','Daily regression start in UTC'),('DEMO_WINDOW_SECONDS','Regression window seconds (maximum 600)'),('ENABLE_SYNTHETICS','Enable hosted daily API timer (true/false)'),('ENABLE_PROVIDER_FORWARDING','Enable bounded Azure log/metric bridge (true/false)'),('RECOMMENDER_URL','Controlled GCP recommender base URL (blank uses fixture)'),('DOWNSTREAM_AUTH_TOKEN','Optional controlled dependency bearer token')]
 if not args.non_interactive:
  print('Use your own Corridora tenant by default. Demo credentials must come from its authorized operator. Never enter account passwords.')
  for key,label in prompts:
   current=cfg[key]
   value=getpass.getpass(label+(' [saved]' if current else '')+': ') if key in SECRET_NAMES else input(label+' ['+current+']: ')
   if value:cfg[key]=value
  if cfg['SAMPLE_MODE']=='cloud':
   for key,label in [('AZURE_SUBSCRIPTION_ID','Azure subscription ID (existing az login)'),('AZURE_LOCATION','Azure Flex-supported region'),('AZURE_RESOURCE_GROUP','Dedicated or selected shared resource group'),('EXISTING_RESOURCE_GROUP','Use existing shared resource group (true/false)'),('RESOURCE_PREFIX','Unique app resource prefix'),('AZURE_EXISTING_COSMOS_NAME','Existing Cosmos account in selected group (blank creates account)'),('COSMOS_FREE_TIER','Eligible subscription free-tier slot available (true/false)'),('IMAGE_REPOSITORY','Public GHCR image repository (existing docker login)')]:
    value=input(label+' ['+cfg[key]+']: ')
    if value:cfg[key]=value
 if cfg['CORRIDORA_TARGET']=='demo':cfg['DEPLOYMENT_ENVIRONMENT']='demo'
 if not cfg['DEMO_CONTROL_TOKEN']:cfg['DEMO_CONTROL_TOKEN']=secrets.token_urlsafe(32)
 validate(cfg)
 protected_write(LOCAL/'config.json',{k:v for k,v in cfg.items() if k not in SECRET_NAMES})
 protected_write(LOCAL/'secrets.json',{k:v for k,v in cfg.items() if k in SECRET_NAMES})
 print('Configuration saved. Secret values are masked and stored owner-only under .local/.')
 return cfg

def environment(cfg):
 env=os.environ.copy();env.update(cfg)
 env['DOTNET_CLI_HOME']=str(LOCAL/'dotnet');env['NUGET_PACKAGES']=str(LOCAL/'nuget');env['DOTNET_CLI_TELEMETRY_OPTOUT']='1';env['DOTNET_SKIP_FIRST_TIME_EXPERIENCE']='1';env['SAMPLE_DATA_DIR']=str(LOCAL/'data');env['PUBLIC_URL']=cfg.get('PUBLIC_URL') or f"http://localhost:{cfg['PORT']}"
 env['npm_config_cache']=str(LOCAL/'npm')
 env.setdefault('PLAYWRIGHT_BROWSERS_PATH',str(LOCAL/'browsers'))
 return env

def execute(argv,cfg,cwd=ROOT,capture=False):
 # Secrets travel through environment/protected parameter files, never process argv.
 result=subprocess.run(argv,cwd=cwd,env=environment(cfg),text=True,stdout=subprocess.PIPE if capture else None,stderr=subprocess.PIPE if capture else None)
 if result.returncode:
  if capture:
   message=(result.stderr or result.stdout or '')[-4000:]
   for key in SECRET_NAMES:
    if cfg.get(key):message=message.replace(cfg[key],'[REDACTED]')
   raise RuntimeError('Command failed: '+argv[0]+'\n'+message)
  raise RuntimeError('Command failed: '+argv[0])
 return json.loads(result.stdout) if capture and result.stdout.strip() else None

def digest():
 h=hashlib.sha256()
 for root in ('src','web/src','infra'):
  for path in sorted((ROOT/root).rglob('*')):
   if path.is_file() and not any(p in ('bin','obj','wwwroot') for p in path.parts):h.update(path.relative_to(ROOT).as_posix().encode());h.update(path.read_bytes())
 return h.hexdigest()[:16]

def build(cfg):
 execute(['npm','ci','--ignore-scripts'],cfg,ROOT/'web')
 execute(['npm','run','build'],cfg,ROOT/'web')
 target=ROOT/'src/Fitness.Api/wwwroot'
 shutil.rmtree(target,ignore_errors=True);shutil.copytree(ROOT/'web/dist',target)
 execute(['dotnet','publish','src/Fitness.Api/Fitness.Api.csproj','-c','Release','-o',str(LOCAL/'publish'),'-p:SourceRevisionId='+digest(),'-p:RestoreLockedMode=true','--nologo'],cfg)

def inventory():
 return json.loads((LOCAL/'resources.json').read_text()) if (LOCAL/'resources.json').exists() else {}

def az(cfg,*argv):
 return execute(['az',*argv,'--subscription',cfg['AZURE_SUBSCRIPTION_ID'],'--output','json','--only-show-errors'],cfg,capture=True)

def plan(cfg,inv):
 return {'subscription':cfg['AZURE_SUBSCRIPTION_ID'],'region':cfg['AZURE_LOCATION'],'resource_group':cfg['AZURE_RESOURCE_GROUP'],'shared_group':cfg['EXISTING_RESOURCE_GROUP']=='true','owner':inv.get('owner','new-installation'),'image':cfg['IMAGE_REPOSITORY']+':'+digest(),'resources':['Container Apps Consumption (0..2 replicas, 0.25 CPU/0.5 GiB)','Functions Flex (.NET 10, 512 MiB, no Always Ready; Azure minimum maximumInstanceCount 40)','Cosmos provisioned free tier 400 RU/s; dedicated database','Storage LRS exports, bounded queue/poison queue, function package','User-assigned identity and Key Vault scoped secrets']+(['separate diagnostic storage, 7-day lifecycle, 15-minute bridge'] if cfg['ENABLE_PROVIDER_FORWARDING']=='true' else []),'monthly_estimate_usd':'0–2 incremental under tiny demo traffic and an eligible Cosmos free slot; GHCR public image avoids paid ACR; alerts are not billing caps','operations':['docker buildx --platform linux/amd64 --push','ARM/Bicep resource deployment','dotnet publish Functions and OneDeploy zip','seed workflow via app APIs']}

def ensure_public_image(image):
 from urllib.parse import quote
 repository,tag=image.removeprefix('ghcr.io/').rsplit(':',1)
 token_url='https://ghcr.io/token?service=ghcr.io&scope='+quote('repository:'+repository+':pull',safe='')
 try:
  with urllib.request.urlopen(token_url,timeout=15) as response:token=json.load(response)['token']
  request=urllib.request.Request('https://ghcr.io/v2/'+repository+'/manifests/'+tag,headers={'Authorization':'Bearer '+token,'Accept':'application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.v2+json'})
  with urllib.request.urlopen(request,timeout=15) as response:
   if response.status!=200:raise ValueError('Public image manifest unavailable')
 except (OSError,ValueError,KeyError) as error:raise ValueError('Pushed GHCR image is not anonymously readable. Set its GitHub package visibility to public, then rerun deploy; Azure resources have not been applied yet.') from error

def deploy(cfg,args):
 if not args.plan:validate(cfg)
 if cfg['SAMPLE_MODE']=='local':
  if args.plan:print(json.dumps({'mode':'local','port':cfg['PORT'],'actions':['locked npm build','locked .NET publish','persist synthetic state in .local/data']},indent=2));return
  build(cfg); print('Local app built; run ./setup run');return
 inv=inventory()
 print(json.dumps(plan(cfg,inv),indent=2))
 if args.plan:return
 for tool in ('az','docker','dotnet'):
  if not shutil.which(tool):raise ValueError(tool+' is required for cloud deployment')
 account=az(cfg,'account','show')
 print('Authenticated Azure account:',account.get('name'),account.get('id'))
 if not args.yes and input('Apply this reviewed deployment plan? [y/N] ').strip().lower()!='y':return
 if not inv:
  inv={'owner':secrets.token_hex(12),'resource_group':cfg['AZURE_RESOURCE_GROUP'],'subscription':cfg['AZURE_SUBSCRIPTION_ID'],'group_owned':cfg['EXISTING_RESOURCE_GROUP']!='true','stage':'planned'};protected_write(LOCAL/'resources.json',inv)
 if inv['subscription']!=cfg['AZURE_SUBSCRIPTION_ID'] or inv['resource_group']!=cfg['AZURE_RESOURCE_GROUP']:raise ValueError('Inventory belongs to a different subscription/group; use a separate clone for a second installation')
 for namespace in ('Microsoft.App','Microsoft.ManagedIdentity','Microsoft.DocumentDB','Microsoft.Storage','Microsoft.KeyVault','Microsoft.Web','Microsoft.Insights'):
  az(cfg,'provider','register','--namespace',namespace,'--wait')
 exists=az(cfg,'group','exists','--name',cfg['AZURE_RESOURCE_GROUP'])
 if not exists:
  if not inv['group_owned']:raise ValueError('Selected shared resource group does not exist')
  az(cfg,'group','create','--name',cfg['AZURE_RESOURCE_GROUP'],'--location',cfg['AZURE_LOCATION'],'--tags','corridora-sample=fitness','corridora-owner='+inv['owner'])
 else:
  group=az(cfg,'group','show','--name',cfg['AZURE_RESOURCE_GROUP'])
  if inv['group_owned'] and group.get('tags',{}).get('corridora-owner')!=inv['owner']:raise ValueError('Existing group has no matching ownership tag; select it explicitly as shared or choose a new group')
 resources=az(cfg,'resource','list','--resource-group',cfg['AZURE_RESOURCE_GROUP'])
 for resource in resources:
  if resource['name'].startswith(cfg['RESOURCE_PREFIX']+'-') and resource['name']!=cfg['AZURE_EXISTING_COSMOS_NAME'] and resource.get('tags',{}).get('corridora-owner')!=inv['owner']:
   raise ValueError('Resource prefix overlaps an unowned resource: '+resource['name'])
 if cfg['AZURE_EXISTING_COSMOS_NAME']:
  cosmos=az(cfg,'cosmosdb','show','--resource-group',cfg['AZURE_RESOURCE_GROUP'],'--name',cfg['AZURE_EXISTING_COSMOS_NAME'])
  if not cosmos.get('enableFreeTier'):raise ValueError('Selected Cosmos account has no provisioned free tier; revise budget explicitly first')
  if inv['group_owned']:raise ValueError('Use EXISTING_RESOURCE_GROUP=true when reusing a Cosmos account; the shared group must survive teardown')
 inv['stage']='group-ready';protected_write(LOCAL/'resources.json',inv)
 image=cfg['IMAGE_REPOSITORY']+':'+digest()
 execute(['docker','buildx','build','--platform','linux/amd64','--build-arg','SOURCE_REVISION='+digest(),'-t',image,'--push','.'],cfg)
 ensure_public_image(image)
 inv['stage']='image-pushed';inv['image']=image;protected_write(LOCAL/'resources.json',inv)
 hour,minute=map(int,cfg['DEMO_DAILY_START_UTC'].split(':'));daily=(hour*60+minute+1)%1440;watch=(hour*60+minute+20)%1440
 parameters={'prefix':cfg['RESOURCE_PREFIX'],'owner':inv['owner'],'location':cfg['AZURE_LOCATION'],'image':image,'existingCosmosName':cfg['AZURE_EXISTING_COSMOS_NAME'],'cosmosFreeTier':cfg['COSMOS_FREE_TIER']=='true','forwarding':cfg['ENABLE_PROVIDER_FORWARDING']=='true','synthetics':cfg['ENABLE_SYNTHETICS']=='true','otlpEndpoint':cfg['CORRIDORA_OTLP_ENDPOINT'],'rumScript':cfg['CORRIDORA_RUM_SCRIPT_URL'],'rumKey':cfg['CORRIDORA_RUM_KEY'],'serviceNamespace':cfg['OTEL_SERVICE_NAMESPACE'],'deploymentEnvironment':cfg['DEPLOYMENT_ENVIRONMENT'],'dailyStart':cfg['DEMO_DAILY_START_UTC'],'windowSeconds':int(cfg['DEMO_WINDOW_SECONDS']),'dailyTimer':f'0 {daily%60} {daily//60} * * *','watchdogTimer':f'0 {watch%60} {watch//60} * * *','recommenderUrl':cfg['RECOMMENDER_URL'],'ingestKey':cfg['CORRIDORA_INGEST_KEY'],'controlToken':cfg['DEMO_CONTROL_TOKEN'],'downstreamToken':cfg['DOWNSTREAM_AUTH_TOKEN']}
 parameter_file=LOCAL/'deployment.parameters.json';protected_write(parameter_file,{'$schema':'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#','contentVersion':'1.0.0.0','parameters':{k:{'value':v} for k,v in parameters.items()}})
 try:
  deployment=az(cfg,'deployment','group','create','--resource-group',cfg['AZURE_RESOURCE_GROUP'],'--name',cfg['RESOURCE_PREFIX']+'-fitness','--template-file',str(ROOT/'infra/main.bicep'),'--parameters','@'+str(parameter_file))
 finally:parameter_file.unlink(missing_ok=True)
 outputs={k:v['value'] for k,v in deployment['properties']['outputs'].items()};inv.update(outputs);inv['stage']='resources-ready';protected_write(LOCAL/'resources.json',inv)
 cfg['PUBLIC_URL']=outputs['publicUrl'];protected_write(LOCAL/'config.json',{k:v for k,v in cfg.items() if k not in SECRET_NAMES})
 execute(['dotnet','publish','src/Fitness.Worker/Fitness.Worker.csproj','-c','Release','-o',str(LOCAL/'worker'),'-p:SourceRevisionId='+digest(),'-p:RestoreLockedMode=true','--nologo'],cfg)
 package=LOCAL/'worker.zip'
 with zipfile.ZipFile(package,'w',zipfile.ZIP_DEFLATED) as zipped:
  for path in (LOCAL/'worker').rglob('*'):
   if path.is_file():zipped.write(path,path.relative_to(LOCAL/'worker'))
 az(cfg,'functionapp','deployment','source','config-zip','--resource-group',cfg['AZURE_RESOURCE_GROUP'],'--name',outputs['workerName'],'--src',str(package))
 inv['stage']='deployed';protected_write(LOCAL/'resources.json',inv)
 print('Deployed:',cfg['PUBLIC_URL']);print('Set GHCR package visibility to public. Enable scheduled browser workflow with dedicated secrets. Run ./setup verify after role propagation.')

def request(cfg,path,body=None):
 url=(cfg.get('PUBLIC_URL') or f"http://localhost:{cfg['PORT']}")+path
 headers={'Authorization':'Bearer '+cfg['DEMO_CONTROL_TOKEN'],'Content-Type':'application/json'}
 req=urllib.request.Request(url,data=json.dumps(body).encode() if body is not None else None,headers=headers)
 with urllib.request.urlopen(req,timeout=15) as result:return json.load(result)

def run(cfg,args):
 if cfg['SAMPLE_MODE']=='cloud':print(json.dumps(request(cfg,'/healthz'),indent=2));print(cfg['PUBLIC_URL']);return
 if not (LOCAL/'publish/Fitness.Api.dll').exists():build(cfg)
 print(f"Open http://localhost:{cfg['PORT']} · Ctrl-C to stop")
 execute(['dotnet',str(LOCAL/'publish/Fitness.Api.dll')],cfg,LOCAL/'publish')

def verify(cfg,args):
 print(json.dumps(request(cfg,'/healthz'),indent=2))
 if not os.environ.get('PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH'):
  execute(['node',str(ROOT/'web/node_modules/playwright/cli.js'),'install','chromium'],cfg,ROOT/'web')
 execute(['node',str(ROOT/'synthetics/journey.mjs')],cfg,ROOT/'web')
 print('Browser journey completed. In Corridora, match actual browser CLIENT span ID to backend parent span ID and inspect queue retry/poison/recovery. Shared stored telemetry is not verified by this local command.')

def destroy(cfg,args):
 if cfg['SAMPLE_MODE']=='local':
  if args.plan:print('Would remove only sample .local/data and build outputs; configuration preserved.');return
  shutil.rmtree(LOCAL/'data',ignore_errors=True);shutil.rmtree(LOCAL/'publish',ignore_errors=True);print('Local generated data/build removed.');return
 inv=inventory()
 if not inv:
  if args.plan:print('No recorded owned Azure resources. Destruction would require an ownership inventory; no resources selected.');return
  raise ValueError('No ownership inventory; refusing to remove resources')
 print(json.dumps({'group':inv['resource_group'],'group_owned':inv['group_owned'],'owner':inv['owner'],'preserve':'shared Cosmos account, shared group, public GHCR images and cross-cloud dependencies'},indent=2))
 if args.plan:return
 if not args.yes and input('Delete only this sample-owned installation? [y/N] ').strip().lower()!='y':return
 cfg=cfg|{'AZURE_SUBSCRIPTION_ID':inv['subscription'],'AZURE_RESOURCE_GROUP':inv['resource_group']}
 if inv['group_owned']:
  group=az(cfg,'group','show','--name',inv['resource_group'])
  if group.get('tags',{}).get('corridora-owner')!=inv['owner']:raise ValueError('Ownership tag mismatch; refusing deletion')
  resources=az(cfg,'resource','list','--resource-group',inv['resource_group'])
  if any(r.get('tags',{}).get('corridora-owner')!=inv['owner'] and r['type'].lower()!='microsoft.authorization/roleassignments' for r in resources):raise ValueError('Group contains unowned resources; refusing group deletion. Move shared resources or remove the app-owned resources manually.')
  # An explicitly reused Cosmos account is never deleted with a group.
  if cfg['AZURE_EXISTING_COSMOS_NAME']:raise ValueError('Group contains selected shared Cosmos; use a shared group configuration to preserve it')
  az(cfg,'group','delete','--name',inv['resource_group'],'--yes')
 else:
  # Delete child database/RBAC from recorded IDs, then only matching tagged resources in reverse dependency order.
  child_ids=[rid for rid in inv.get('ownedResourceIds',[]) if any(part in rid for part in ('/sqlDatabases/','/sqlRoleAssignments/','/roleAssignments/','/diagnosticSettings/'))]
  child_ids.sort(key=lambda rid: '/sqlDatabases/' in rid)
  for rid in child_ids:az(cfg,'resource','delete','--ids',rid)
  resources=az(cfg,'resource','list','--resource-group',inv['resource_group'])
  priority={'Microsoft.App/containerApps':0,'Microsoft.Web/sites':1,'Microsoft.Web/serverfarms':2,'Microsoft.App/managedEnvironments':3,'Microsoft.ManagedIdentity/userAssignedIdentities':9}
  for resource in sorted(resources,key=lambda r:priority.get(r['type'],5)):
   if resource.get('tags',{}).get('corridora-owner')==inv['owner']:az(cfg,'resource','delete','--ids',resource['id'])
 (LOCAL/'resources.json').unlink();print('Owned Azure resources removed; Key Vault soft-delete retention remains 7 days; shared resources preserved. Remove old public GHCR versions separately.')

def main():
 parser=argparse.ArgumentParser(description='Independent Angular/.NET Azure fitness sample. No command starts a credential lookup except an explicit cloud deploy/run/destroy.')
 parser.add_argument('command',nargs='?',choices=['configure','deploy','run','verify','scenario','destroy'])
 parser.add_argument('--non-interactive',action='store_true');parser.add_argument('--config');parser.add_argument('--plan','--dry-run',action='store_true');parser.add_argument('--yes',action='store_true');parser.add_argument('--phase',choices=['healthy','regression','recover'],default='regression');parser.add_argument('--duration',type=int,default=120)
 args=parser.parse_args()
 try:
  if not args.command:
   cfg=configure(args);deploy(cfg,args)
   if cfg['SAMPLE_MODE']=='local':
    process=subprocess.Popen(['dotnet',str(LOCAL/'publish/Fitness.Api.dll')],cwd=LOCAL/'publish',env=environment(cfg))
    try:
     for _ in range(100):
      try:request(cfg,'/healthz');break
      except OSError:time.sleep(.1)
     verify(cfg,args);print('Sample running. Press Ctrl-C to stop.');process.wait()
    finally:process.terminate();process.wait(timeout=10)
    return
   verify(cfg,args);return
  if args.command=='configure':configure(args);return
  cfg=load(args.config)
  if not (args.plan and args.command in ('deploy','destroy')):validate(cfg)
  if args.command=='scenario':print(json.dumps(request(cfg,'/api/demo/scenario',{'phase':args.phase,'duration_seconds':args.duration}),indent=2));return
  globals()[args.command](cfg,args)
 except (ValueError,RuntimeError,OSError,urllib.error.HTTPError) as error:print('Setup error:',str(error),file=sys.stderr);raise SystemExit(1)
 except KeyboardInterrupt:print('\nStopped. Configuration preserved.')
if __name__=='__main__':main()
