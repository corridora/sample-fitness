import importlib.util
import json
import os
from pathlib import Path
import tempfile
import io
import contextlib
import sys
import unittest
from unittest.mock import patch
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('setup',ROOT/'scripts/setup.py');setup=importlib.util.module_from_spec(spec);spec.loader.exec_module(setup)
class SetupTests(unittest.TestCase):
 def cfg(self):return setup.DEFAULTS|{'DEMO_CONTROL_TOKEN':'mock-control'}
 def test_local_defaults(self):setup.validate(self.cfg())
 def test_demo_needs_operator_keys(self):
  with self.assertRaisesRegex(ValueError,'operator-provisioned'):setup.validate(self.cfg()|{'CORRIDORA_TARGET':'demo'})
 def test_rejects_external_cleartext(self):
  with self.assertRaisesRegex(ValueError,'HTTPS'):setup.validate(self.cfg()|{'RECOMMENDER_URL':'http://external.example'})
 def test_owner_only_json_no_secrets_in_plan(self):
  with tempfile.TemporaryDirectory() as directory,patch.object(setup,'LOCAL',Path(directory)):
   path=Path(directory)/'secrets.json';setup.protected_write(path,{'DEMO_CONTROL_TOKEN':'very-unique-private-value'})
   self.assertEqual(path.stat().st_mode&0o777,0o600)
   self.assertNotIn('very-unique-private-value',json.dumps(setup.plan(self.cfg(),{'owner':'installation'})))
 def test_cloud_plan_without_login(self):
  cfg=self.cfg()|{'SAMPLE_MODE':'cloud','AZURE_SUBSCRIPTION_ID':'test','CORRIDORA_INGEST_KEY':'mock','CORRIDORA_RUM_KEY':'mock'}
  setup.validate(cfg);self.assertIn('GHCR',setup.plan(cfg,{})['monthly_estimate_usd'])
 def test_bounds(self):
  for value in ['0','601','-1']:
   with self.assertRaises(ValueError):setup.validate(self.cfg()|{'DEMO_WINDOW_SECONDS':value})
 def test_credential_free_plans(self):
  for command in ('deploy','destroy'):
   with patch.object(setup,'load',return_value=setup.DEFAULTS.copy()),patch.object(setup,'validate',side_effect=AssertionError('Plan must not require keys')),patch.object(sys,'argv',['setup',command,'--plan']),contextlib.redirect_stdout(io.StringIO()) as output:
    setup.main();self.assertTrue(output.getvalue())
 def test_bicep_contract(self):
  bicep=(ROOT/'infra/main.bicep').read_text();self.assertIn('enableFreeTier: cosmosFreeTier',bicep);self.assertIn('minReplicas: 0, maxReplicas: 2',bicep);self.assertIn("version: '10.0'",bicep);self.assertNotIn('alwaysReady:',bicep);self.assertIn('defaultTtl: 604800',bicep);self.assertIn("name: 'DEMO_WINDOW_SECONDS', value: string(windowSeconds)",bicep)
if __name__=='__main__':unittest.main(verbosity=2)
