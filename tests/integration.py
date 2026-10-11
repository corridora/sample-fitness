#!/usr/bin/env python3
"""Integration proof against a local capture sink; never writes to Demo."""
import http.server
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
EVENTS=[]
LOCK=threading.Lock()
DEPENDENCY_MODE="normal"
DEPENDENCY_HEADERS=[]
class Sink(http.server.BaseHTTPRequestHandler):
 def log_message(self,*a):pass
 def do_OPTIONS(self):
  self.send_response(204);self.send_header('Access-Control-Allow-Origin',self.headers.get('Origin','*'));self.send_header('Access-Control-Allow-Headers','*');self.end_headers()
 def do_POST(self):
  body=self.rfile.read(int(self.headers.get('Content-Length','0')))
  with LOCK:EVENTS.append((self.path,dict(self.headers),body))
  self.send_response(200);self.send_header('Access-Control-Allow-Origin',self.headers.get('Origin','*'));self.send_header('Content-Type','application/json' if body.startswith(b'{') else 'application/x-protobuf');self.end_headers();self.wfile.write(b'{}' if body.startswith(b'{') else b'')

class Dependency(http.server.BaseHTTPRequestHandler):
 def log_message(self,*a):pass
 def do_GET(self):
  mode=DEPENDENCY_MODE;DEPENDENCY_HEADERS.append(dict(self.headers))
  payload=json.dumps({'items':[{'id':'demo-product','score':0.9}],'model_revision':'fixture-v1'}).encode()
  if mode=='oversize':payload=b'x'*70000
  self.send_response(200);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(payload)));self.end_headers()
  if mode=='slow-body':time.sleep(4)
  try:self.wfile.write(payload)
  except (BrokenPipeError,ConnectionResetError):pass

def varint(data,pos):
 n=0;shift=0
 while True:
  b=data[pos];pos+=1;n|=(b&127)<<shift
  if b<128:return n,pos
  shift+=7

def fields(data):
 pos=0
 while pos<len(data):
  key,pos=varint(data,pos);number=key>>3;kind=key&7
  if kind==0:value,pos=varint(data,pos)
  elif kind==2:
   size,pos=varint(data,pos);value=data[pos:pos+size];pos+=size
  elif kind==1:value=data[pos:pos+8];pos+=8
  elif kind==5:value=data[pos:pos+4];pos+=4
  else:raise AssertionError('Unsupported protobuf wire kind')
  yield number,value

def spans():
 result=[]
 with LOCK:events=list(EVENTS)
 for path,headers,body in events:
  if path!='/v1/traces':continue
  if body.startswith(b'{'):
   for resource in json.loads(body).get('resourceSpans',[]):
    for scope in resource.get('scopeSpans',[]):
     for span in scope.get('spans',[]):result.append({'trace':span['traceId'],'span':span['spanId'],'parent':span.get('parentSpanId',''),'name':span['name'],'kind':span.get('kind',0),'browser':True})
   continue
  for number,resource in fields(body):
   if number!=1:continue
   for n,scope in fields(resource):
    if n!=2:continue
    for n2,span in fields(scope):
     if n2!=2:continue
     values=dict(fields(span));result.append({'trace':values.get(1,b'').hex(),'span':values.get(2,b'').hex(),'parent':values.get(4,b'').hex(),'name':values.get(5,b'').decode(),'kind':values.get(6,0),'browser':False})
 return result

def free_port():
 with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]

class Integration(unittest.TestCase):
 @classmethod
 def setUpClass(cls):
  cls.temp=tempfile.TemporaryDirectory(prefix='fitness-integration-');cls.sink=http.server.ThreadingHTTPServer(('127.0.0.1',0),Sink);threading.Thread(target=cls.sink.serve_forever,daemon=True).start()
  cls.dependency=http.server.ThreadingHTTPServer(('127.0.0.1',0),Dependency);threading.Thread(target=cls.dependency.serve_forever,daemon=True).start()
  cls.port=free_port();cls.url=f'http://localhost:{cls.port}';cls.env=os.environ.copy();cls.env.update({'SAMPLE_MODE':'local','PORT':str(cls.port),'PUBLIC_URL':cls.url,'CORRIDORA_OTLP_ENDPOINT':f'http://127.0.0.1:{cls.sink.server_port}','CORRIDORA_INGEST_KEY':'mock-backend','CORRIDORA_RUM_KEY':'mock-browser','DEMO_CONTROL_TOKEN':'mock-control','SAMPLE_DATA_DIR':cls.temp.name,'RECOMMENDER_URL':f'http://127.0.0.1:{cls.dependency.server_port}','DEMO_DAILY_START_UTC':os.environ.get('FITNESS_TEST_DAILY_START','00:00'),'DOTNET_CLI_HOME':str(ROOT/'.local/dotnet'),'NUGET_PACKAGES':str(ROOT/'.local/nuget')})
  if (ROOT/'web/dist').exists():shutil.copytree(ROOT/'web/dist',ROOT/'src/Fitness.Api/wwwroot',dirs_exist_ok=True)
  cls.output=open(ROOT/'.local/integration.log','w')
  cls.process=subprocess.Popen(['dotnet',str(ROOT/'src/Fitness.Api/bin/Debug/net10.0/Fitness.Api.dll')],cwd=ROOT/'src/Fitness.Api',env=cls.env,stdout=cls.output,stderr=subprocess.STDOUT)
  for _ in range(100):
   if cls.process.poll()!=None:raise RuntimeError((ROOT/'.local/integration.log').read_text())
   try:cls.req('/healthz');return
   except (OSError,urllib.error.HTTPError):time.sleep(.1)
  raise RuntimeError('API did not become healthy')
 @classmethod
 def tearDownClass(cls):
  cls.process.terminate();cls.process.wait(timeout=10);cls.output.close();cls.sink.shutdown();cls.sink.server_close();cls.dependency.shutdown();cls.dependency.server_close();cls.temp.cleanup()
 @classmethod
 def req(cls,path,body=None,auth=True,headers=None):
  h={'Content-Type':'application/json'};h.update(headers or {})
  if auth:h['Authorization']='Bearer mock-control'
  request=urllib.request.Request(cls.url+path,data=json.dumps(body).encode() if body is not None else None,headers=h)
  with urllib.request.urlopen(request,timeout=15) as response:return json.load(response),response.headers
 def scenario(self,phase,duration=120):return self.req('/api/demo/scenario',{'phase':phase,'duration_seconds':duration})[0]
 def summary(self,workout):
  job=self.req('/api/summaries',{'workout_id':workout})[0]
  for _ in range(40):
   job=self.req('/api/summaries/'+job['id'])[0]
   if job['status'] in ('complete','poison'):return job
   time.sleep(.2)
  self.fail('Summary failed to finish')
 def test_01_context_and_three_signals(self):
  trace='1234567890abcdef1234567890abcdef';parent='1122334455667788'
  body,headers=self.req('/api/trace-check',headers={'traceparent':f'00-{trace}-{parent}-01'})
  self.assertEqual(body['trace_id'],trace);self.assertEqual(headers['Cache-Control'],'no-store')
  self.req('/api/workouts');time.sleep(.3)
  capture=spans();self.assertTrue(any(s['trace']==trace and s['parent']==parent and s['kind']==2 for s in capture))
  with LOCK:events=list(EVENTS)
  self.assertTrue({'/v1/traces','/v1/logs','/v1/metrics'}.issubset({e[0] for e in events}))
  self.assertTrue(all(dict((k.lower(),v) for k,v in h.items()).get('x-corridora-key')=='mock-backend' for _,h,b in events if not b.startswith(b'{')))
 def test_02_auth_and_bounds(self):
  for auth,body in [(False,{'phase':'regression','duration_seconds':120}),(True,{'phase':'regression','duration_seconds':601}),(True,{'phase':'regression','duration_seconds':0})]:
   with self.assertRaises(urllib.error.HTTPError) as caught:self.req('/api/demo/scenario',body,auth=auth)
   self.assertIn(caught.exception.code,(400,401));caught.exception.close()
 def test_03_poison_control_recovery_and_parentage(self):
  self.scenario('regression');bad=self.summary('demo-workout');self.assertEqual(bad['status'],'poison');self.assertEqual(bad['attempts'],3)
  control=self.summary('healthy-workout');self.assertEqual(control['status'],'complete')
  self.scenario('recover');good=self.summary('demo-workout');self.assertEqual(good['status'],'complete')
  time.sleep(.2);capture=spans();producers=[s for s in capture if s['name']=='weekly-summary enqueue'];consumers=[s for s in capture if s['name']=='weekly-summary process'];self.assertTrue(any(c['parent']==p['span'] and c['trace']==p['trace'] for p in producers for c in consumers))
 def test_04_expiry(self):
  self.scenario('regression',1);time.sleep(1.1);effective=self.req('/api/demo/status')[0];self.assertTrue(effective['scheduled']);self.assertEqual(self.summary('demo-workout')['status'],'poison' if effective['phase']=='regression' else 'complete')
 def test_05_rum_frontend_and_no_secret_leak(self):
  with urllib.request.urlopen(self.url) as response:html=response.read().decode();csp=response.headers['Content-Security-Policy']
  self.assertIn('ckid=mock-browser',html);self.assertIn('data-trace-origins="'+self.url+'"',html);self.assertNotIn('mock-backend',html);self.assertIn("script-src 'self' https://rum.corridora.com",csp)
 def test_06_real_browser_journey(self):
  env=self.env.copy()
  if os.environ.get('RUM_BUNDLE_PATH'):env['RUM_BUNDLE_PATH']=os.environ['RUM_BUNDLE_PATH']
  else:env['CORRIDORA_RUM_KEY']='';env['DISABLE_RUM_FOR_JOURNEY']='true'
  result=subprocess.run(['node',str(ROOT/'synthetics/journey.mjs')],cwd=ROOT/'web',env=env,text=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
  self.assertEqual(result.returncode,0,result.stdout);print(result.stdout)
 def test_07_real_rum_correlation(self):
  if not os.environ.get('RUM_BUNDLE_PATH'):self.skipTest('Set RUM_BUNDLE_PATH to a genuine Corridora distribution for offline browser correlation proof')
  time.sleep(.5);capture=spans();client=[s for s in capture if s['browser'] and s['kind']==3];server=[s for s in capture if not s['browser'] and s['kind']==2]
  self.assertTrue(any(s['trace']==c['trace'] and s['parent']==c['span'] for c in client for s in server),'Actual RUM CLIENT must parent backend SERVER')
 def test_08_controlled_dependency_bounds(self):
  global DEPENDENCY_MODE
  try:
   for mode in ('oversize','slow-body'):
    DEPENDENCY_MODE=mode;start=time.monotonic();body=self.req('/api/recommendations')[0]
    self.assertTrue(body['fallback']);self.assertLess(time.monotonic()-start,5.5)
   headers={k.lower():v for k,v in DEPENDENCY_HEADERS[-1].items()}
   self.assertRegex(headers['traceparent'],r'^00-[0-9a-f]{32}-[0-9a-f]{16}-01$')
   self.assertNotIn('x-corridora-key',headers);self.assertNotIn('authorization',headers)
  finally:DEPENDENCY_MODE='normal'
if __name__=='__main__':unittest.main(verbosity=2)
