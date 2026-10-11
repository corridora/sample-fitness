#!/usr/bin/env python3
"""Watchdog replays the complete browser workflow when today's heartbeat is absent."""
import json
import os
from pathlib import Path
import subprocess
import sys
import urllib.request
from datetime import datetime, timezone
base=os.environ['PUBLIC_URL'];token=os.environ['DEMO_CONTROL_TOKEN']
watchdog='--watchdog' in sys.argv
if watchdog:
 try:
  request=urllib.request.Request(base+'/api/demo/heartbeat',headers={'Authorization':'Bearer '+token})
  with urllib.request.urlopen(request,timeout=15) as response:heartbeat=json.load(response)
  if heartbeat and heartbeat.get('day')==datetime.now(timezone.utc).strftime('%Y-%m-%d'):
   print('Today already has a successful full browser heartbeat.');sys.exit(0)
 except (OSError,ValueError):pass
 print('Heartbeat missing; replaying bounded regression, healthy control and recovery now.')
root=Path(__file__).resolve().parents[1]
subprocess.run(['node',str(root/'synthetics/journey.mjs')],cwd=root/'web',check=True)
