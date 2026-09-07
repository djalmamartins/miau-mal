"""Exercise built executables, including real Kestrel binding. No third-party packages."""
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parent.parent
DOTNET = os.environ.get('MIAU_DOTNET', 'dotnet')
CLI = ROOT / 'src/Miau.Cli/bin/Release/net10.0/miau.dll'
API = ROOT / 'src/Miau.Api/bin/Release/net10.0/Miau.Api.dll'

for args, expected in [([], 0), (['--help'], 0), (['--version'], 0), (['run'], 2), (['--version', 'extra'], 2)]:
    result = subprocess.run([DOTNET, str(CLI), *args], capture_output=True, text=True, timeout=15)
    assert result.returncode == expected, result
    if args == ['--version']:
        assert result.stdout.strip() == '0.1.0-dev', result.stdout
    if expected == 2:
        assert result.stderr.strip(), 'Invalid command must explain failure'
print('PASS: 5 CLI cases')

# Refuse to accidentally validate or terminate an unrelated API already using the port.
with socket.socket() as probe:
    probe.bind(('127.0.0.1', 11435))

env = dict(os.environ, ASPNETCORE_URLS='http://0.0.0.0:11436',
           Kestrel__Endpoints__External__Url='http://0.0.0.0:11437',
           DOTNET_CLI_TELEMETRY_OPTOUT='1')
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
with tempfile.TemporaryFile(mode='w+', encoding='utf-8') as log:
    process = subprocess.Popen([DOTNET, str(API)], cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT)
    try:
        deadline = time.monotonic() + 20
        while True:
            assert process.poll() is None, 'API exited before health check'
            try:
                with opener.open('http://127.0.0.1:11435/health', timeout=1) as response:
                    assert response.status == 200
                    assert json.load(response) == {'status': 'ok', 'version': '0.1.0-dev'}
                break
            except (urllib.error.URLError, TimeoutError):
                if time.monotonic() >= deadline:
                    raise AssertionError('API failed to become healthy')
                time.sleep(0.1)
        log.seek(0)
        output = log.read()
        bindings = [line.strip() for line in output.splitlines() if 'Now listening on:' in line]
        assert bindings == ['Now listening on: http://127.0.0.1:11435'], output
        print('PASS: real HTTP health and loopback binding despite URL/Kestrel overrides')
    finally:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
