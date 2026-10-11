#!/usr/bin/env python3
"""Run the documented HTTP workflow against an already running API. Python 3.10+, no packages."""
import argparse
import json
import secrets
import tempfile
import urllib.error
import urllib.request
import uuid
import zipfile
import io
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--base-url', default='http://localhost:5000')
parser.add_argument('--output', default='artifacts/smoke-export.zip')
args = parser.parse_args()
base = args.base_url.rstrip('/')
bearer = None

def call(method, path, body=None, public=False, content_type='application/json', raw=False):
    headers = {'Accept': 'application/json'}
    if bearer and not public:
        headers['Authorization'] = 'Bearer ' + bearer
    if body is not None:
        headers['Content-Type'] = content_type
    data = json.dumps(body).encode() if isinstance(body, dict) else body
    request = urllib.request.Request(base + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            payload = response.read()
            return payload if raw else json.loads(payload) if payload else None
    except urllib.error.HTTPError as error:
        # Do not print the request URL: public URLs contain bearer tokens.
        raise RuntimeError(f'HTTP {error.code}: {error.read().decode()}') from None

def upload(path, filename, mime, content):
    boundary = 'provena-' + uuid.uuid4().hex
    payload = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{filename}"\r\nContent-Type: {mime}\r\n\r\n').encode() + content + f'\r\n--{boundary}--\r\n'.encode()
    return call('POST', path, payload, public=True, content_type='multipart/form-data; boundary=' + boundary)

suffix = uuid.uuid4().hex[:10]
email = 'smoke-' + suffix + '@example.test'
password = 'Aa9!' + secrets.token_urlsafe(24)
registered = call('POST', '/api/auth/register', {'email': email, 'password': password, 'firstName': 'Smoke', 'lastName': 'Test', 'organizationName': 'Provena smoke ' + suffix}, public=True)
bearer = registered['accessToken']
call('GET', '/api/auth/me')
bearer = call('POST', '/api/auth/login', {'email': email, 'password': password}, public=True)['accessToken']
supplier = call('POST', '/api/suppliers', {'name': 'Coffee Brasil Ltd.', 'email': 'contact@example.test', 'externalReference': 'SUP-001', 'countryCode': 'BR'})
product = call('POST', '/api/products', {'name': 'Arabica Coffee', 'cnCode': '0901', 'commodityType': 'Coffee', 'isActive': True, 'externalReference': 'P-001'})
shipment = call('POST', '/api/shipments', {'referenceNumber': 'IMP-' + suffix, 'supplierId': supplier['id'], 'productId': product['id'], 'quantity': 18500, 'unit': 'Kg', 'originCountryCode': 'BR'})
case = '/api/eudr-cases/' + shipment['eudrCaseId']
assert call('GET', case)['status'] == 'Draft'
link = call('POST', case + '/supplier-request')
token = link['publicUrl'].rsplit('/', 1)[1]
public = '/public/supplier-requests/' + token
call('GET', public, public=True)
call('PUT', public + '/details', {'countryOfProductionCode': 'BR', 'productionPeriodFrom': '2026-01-01', 'productionPeriodTo': '2026-06-30', 'confirmedQuantity': 18500, 'notes': 'Harvest 2026'}, public=True)
upload(public + '/documents', 'declaration.txt', 'text/plain', b'Smoke test supplier declaration.')
upload(public + '/geolocation', 'plots.geojson', 'application/geo+json', b'{"type":"Polygon","coordinates":[[[-48,-20],[-47,-20],[-47,-19],[-48,-20]]]}')
call('POST', public + '/complete', public=True)
details = call('GET', case)
assert details['status'] == 'ReviewNeeded'
for requirement in details['requirements']:
    call('PATCH', case + '/requirements/' + requirement['id'], {'status': 'Accepted', 'reviewerNote': 'Smoke test review.'})
assert call('GET', case)['status'] == 'ReadyForExport'
archive = call('GET', case + '/export', raw=True)
with zipfile.ZipFile(io.BytesIO(archive)) as zipped:
    assert any(name.endswith('manifest.json') for name in zipped.namelist())
    assert any(name.endswith('declaration.txt') for name in zipped.namelist())
    assert any(name.endswith('geolocation/original.geojson') for name in zipped.namelist())
output = Path(args.output)
output.parent.mkdir(parents=True, exist_ok=True)
output.write_bytes(archive)
call('POST', case + '/complete', {'externalDdsReference': 'SMOKE-' + suffix})
assert call('GET', case)['status'] == 'Completed'
print('PASS: register/login -> shipment -> supplier submission -> review -> ZIP -> Completed.')
print('ZIP:', output.resolve())
print('A new test organization was created. No credentials or public tokens were printed or saved.')
