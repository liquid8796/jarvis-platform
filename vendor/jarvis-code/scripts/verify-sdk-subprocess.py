"""Real Python SDK + staged CLI, with a loopback-only scripted Anthropic server."""
import argparse
import contextlib
import hashlib
import importlib.metadata
import asyncio
import json
import os
import shutil
import subprocess
import tempfile
import threading
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from claude_agent_sdk import (ClaudeAgentOptions, ClaudeSDKClient, ResultMessage, StreamEvent, UserMessage, HookMatcher,
                             PermissionResultAllow, PermissionResultDeny, PermissionUpdate,
                             create_sdk_mcp_server, tool)
from claude_agent_sdk.types import PermissionRuleValue
from claude_agent_sdk._internal.transport.subprocess_cli import SubprocessCLITransport

class TraceTransport(SubprocessCLITransport):
    async def write(self, data):
        print('SDK -> CLI', data[:250], flush=True)
        await super().write(data)
    async def read_messages(self):
        async for data in super().read_messages():
            print('CLI -> SDK', str(data)[:400], flush=True)
            yield data

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--cli', required=True, type=Path, help='Built Jarvis CLI executable to verify')
parser.add_argument('--artifacts', type=Path, default=Path(__file__).resolve().parent.parent/'artifacts'/'sdk-subprocess')
args = parser.parse_args()
CLI = args.cli.resolve(strict=True)
ROOT = args.artifacts.resolve()
ROOT.mkdir(parents=True, exist_ok=True)
REQUESTS = []
ECHO_TOOL = 'mcp__fixture__echo'
SERVER_INSTRUCTIONS = 'SDK fixture instructions: report only the controlled echo result.'
class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_): pass
    def do_POST(self):
        print('fixture HTTP request', flush=True)
        body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        REQUESTS.append({'path': self.path, 'body': body, 'beta': self.headers.get('anthropic-beta')})
        text = 'Explain the result.' if 'Predict the user' in json.dumps(body.get('system')) else '{"answer":7}'
        frames = [
            ('message_start', {'type':'message_start','message':{'id':'fixture-'+str(len(REQUESTS)), 'type':'message','role':'assistant','model':body['model'],'content':[], 'usage':{'input_tokens':7,'output_tokens':0}}}),
            ('content_block_start', {'type':'content_block_start','index':0,'content_block':{'type':'text','text':''}}),
            ('content_block_delta', {'type':'content_block_delta','index':0,'delta':{'type':'text_delta','text':text}}),
            ('content_block_stop', {'type':'content_block_stop','index':0}),
            ('message_delta', {'type':'message_delta','delta':{'stop_reason':'end_turn'},'usage':{'output_tokens':4}}),
            ('message_stop', {'type':'message_stop'}),
        ]
        if len(REQUESTS) in (1, 2, 4):
            value = {1:'denied', 2:'allowed', 4:'second'}[len(REQUESTS)]
            frames[1] = ('content_block_start', {'type':'content_block_start','index':0,
                'content_block':{'type':'tool_use','id':f'fixture-tool-{len(REQUESTS)}','name':ECHO_TOOL,'input':{}}})
            frames[2] = ('content_block_delta', {'type':'content_block_delta','index':0,
                'delta':{'type':'input_json_delta','partial_json':json.dumps({'value':value})}})
            frames[4][1]['delta']['stop_reason'] = 'tool_use'
        content = ''.join(f'event: {event}\ndata: {json.dumps(payload)}\n\n' for event,payload in frames).encode()
        self.send_response(200)
        self.send_header('Content-Type','text/event-stream')
        self.send_header('Content-Length',str(len(content)))
        self.end_headers()
        self.wfile.write(content)

async def main():
    server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    profile = 'test-sdk-subprocess-' + uuid.uuid4().hex
    profile_root = Path(os.environ['APPDATA']) / ('JarvisCode-' + profile)
    errors, hooks, received, tool_inputs, permissions = [], [], [], [], []
    settings = json.dumps({'DefaultModelId':'fixture-model',
        'AnthropicBaseUrl':f'http://127.0.0.1:{server.server_port}',
        'ApiKeySets':{'anthropic':['fixture-non-secret']},
        'CustomModels':[{'ProviderId':'anthropic','ModelId':model,'DisplayName':'Fixture',
                         'MaxContextTokens':200000,'InputPricePerMTok':1,'OutputPricePerMTok':2}
                        for model in ('fixture-model','fixture-model-two')]})
    @tool('echo', 'Return the controlled fixture value.', {'value':str})
    async def echo(arguments):
        tool_inputs.append(arguments['value'])
        return {'content':[{'type':'text','text':'controlled echo: '+arguments['value']}]}
    sdk_server = create_sdk_mcp_server(name='fixture', tools=[echo])
    sdk_server['instance'].instructions = SERVER_INSTRUCTIONS
    async def can_use_tool(name, tool_input, context):
        assert name == ECHO_TOOL, name
        permissions.append(tool_input['value'])
        if tool_input['value'] == 'denied':
            return PermissionResultDeny(message='Controlled fixture denial.', interrupt=False)
        return PermissionResultAllow(updated_input={'value':'rewritten'} if tool_input['value'] == 'allowed' else tool_input,
            updated_permissions=[PermissionUpdate(type='addRules', behavior='allow', destination='session',
                rules=[PermissionRuleValue(tool_name=ECHO_TOOL)])])
    async def hook(payload, tool_use_id, context):
        print('fixture hook', payload.get('hook_event_name'), flush=True)
        hooks.append(payload['hook_event_name'])
        return {}
    with tempfile.TemporaryDirectory(prefix='jarvis-sdk-smoke-') as cwd, contextlib.ExitStack() as cleanup:
        cleanup.callback(shutil.rmtree, profile_root, ignore_errors=True)
        cleanup.callback(server.shutdown)
        fixture_env = {name: '' for name in os.environ if name.startswith(
            ('ANTHROPIC_', 'CLAUDE_', 'AWS_', 'GOOGLE_', 'GCLOUD_', 'AZURE_', 'OPENAI_', 'OLLAMA_'))}
        fixture_env.update({'JARVISCODE_PROFILE':profile, 'ANTHROPIC_API_KEY':'fixture-non-secret',
            'ANTHROPIC_BASE_URL':f'http://127.0.0.1:{server.server_port}',
            'CLAUDE_CONFIG_DIR':str(Path(cwd)/'claude-config')})
        options = ClaudeAgentOptions(cli_path=str(CLI), cwd=cwd, settings=settings, setting_sources=[],
            strict_mcp_config=True, model='fixture-model', tools=[ECHO_TOOL], permission_mode='default',
            mcp_servers={'fixture':sdk_server}, can_use_tool=can_use_tool,
            env=fixture_env, include_partial_messages=True,
            extra_args={'debug-file':str(ROOT/'sdk-debug.log'), 'replay-user-messages':None},
            enable_file_checkpointing=True,
            max_budget_usd=.1, output_format={'type':'json_schema','schema':{'type':'object','properties':{'answer':{'type':'integer'}},'required':['answer'],'additionalProperties':False}},
            hooks={event:[HookMatcher(hooks=[hook])] for event in ('UserPromptSubmit','PreModelSwitch','PostModelSwitch')},
            stderr=errors.append)
        async with ClaudeSDKClient(options=options, transport=TraceTransport(prompt=None, options=options)) as client:
            print('SDK initialized', flush=True)
            await client.query('fixture SDK prompt')
            async for message in client.receive_response():
                print('SDK message', type(message).__name__, flush=True)
                received.append(message)
            first = [message for message in received if isinstance(message, ResultMessage)][-1]
            assert not first.is_error, first
            assert first.structured_output == {'answer':7}, first
            assert first.total_cost_usd is not None and first.total_cost_usd > 0, first
            assert any(isinstance(message, StreamEvent) for message in received), received
            assert 'UserPromptSubmit' in hooks, hooks
            await client.set_permission_mode('plan')
            await client.set_permission_mode('default')
            # Manual/default still asks for every mutating call. Auto honors
            # the callback's explicit session-scoped allow rule.
            await client.set_permission_mode('auto')
            await client.set_model('fixture-model-two')
            await client.query('second fixture SDK prompt')
            async for message in client.receive_response(): received.append(message)
            context_usage = await client.get_context_usage()
            assert context_usage['model'] == 'fixture-model-two', context_usage
            assert 0 < context_usage['totalTokens'] <= context_usage['maxTokens'], context_usage
            assert sum(category['tokens'] for category in context_usage['categories']) == context_usage['totalTokens'], context_usage
            checkpoint_id = next(message.uuid for message in received if isinstance(message, UserMessage)
                                 and message.uuid and 'fixture SDK prompt' in str(message.content))
            await client.rewind_files(checkpoint_id)
            try: await client.stop_task('missing-fixture-task')
            except Exception as error:
                assert 'belongs to this session' in str(error), error
            else: raise AssertionError('Unknown task id was accepted')
        sdk_results = [message for message in received if isinstance(message, ResultMessage)]
        assert len(sdk_results) == 2 and all(not result.is_error and result.structured_output == {'answer':7}
                                           for result in sdk_results), sdk_results
        assert tool_inputs == ['rewritten','second'], tool_inputs
        assert permissions == ['denied','allowed'], permissions
        assert hooks == ['UserPromptSubmit','PreModelSwitch','PostModelSwitch','UserPromptSubmit'], hooks
        assert SERVER_INSTRUCTIONS in json.dumps(REQUESTS[0]['body']), 'SDK server instructions did not reach model context'
        assert ECHO_TOOL in [item['name'] for item in REQUESTS[0]['body'].get('tools', [])], 'Explicit SDK tool schema was not advertised'
        env = {**os.environ, **fixture_env}
        def run(*args, **kwargs):
            return subprocess.run([str(CLI), *args], cwd=cwd, env=env, capture_output=True, text=True,
                                  encoding='utf-8', timeout=30, **kwargs)
        def stop_fixture_hosts():
            directory = profile_root / 'cli-background'
            for entry in directory.iterdir() if directory.exists() else []:
                try: uuid.UUID(entry.name)
                except ValueError: continue
                run('stop', entry.name)
        cleanup.callback(stop_fixture_hosts)
        started = run('--bg','--bare','--tools','','--settings',settings,'background fixture prompt', input='')
        assert started.returncode == 0, (started.stdout,started.stderr)
        background_id = started.stdout.strip()
        uuid.UUID(background_id)
        log_path = profile_root / 'cli-background' / background_id / 'output.jsonl'
        for _ in range(150):
            if log_path.exists() and '"type":"result"' in log_path.read_text(encoding='utf-8'): break
            await asyncio.sleep(.1)
        else: raise AssertionError(log_path.read_text(encoding='utf-8') if log_path.exists() else 'No log')
        listing = run('agents','--json')
        assert background_id in listing.stdout, listing
        attached = run('attach',background_id,input='another fixture prompt\n/detach\n')
        assert attached.returncode == 0 and '"type":"result"' in attached.stdout, (attached.stdout,attached.stderr)
        attached_results = [json.loads(line) for line in attached.stdout.splitlines()
                            if line.startswith('{') and json.loads(line).get('type') == 'result']
        assert len(attached_results) == 2 and all(not result['is_error'] for result in attached_results), attached_results
        stopped = run('stop',background_id)
        assert stopped.returncode == 0, stopped
        removed = run('rm',background_id)
        assert removed.returncode == 0 and not log_path.exists(), removed
        assert len(REQUESTS) == 7, len(REQUESTS)
        result = {'sdk_version':importlib.metadata.version('claude-agent-sdk'),
                  'cli_path':str(CLI),'cli_sha256':hashlib.sha256(CLI.read_bytes()).hexdigest(),
                  'cli_assembly_sha256':hashlib.sha256(CLI.with_suffix('.dll').read_bytes()).hexdigest(),'sdk_results':2,'sdk_message_types':sorted({type(message).__name__ for message in received}),
                  'hook_events':hooks,'background_start_attach_stop_rm':'passed','http_requests':len(REQUESTS),
                  'hosted_mcp_tool_inputs':tool_inputs,'permission_callbacks':permissions,
                  'session_permission_update_reused':True,'mcp_instructions_reached_model':True,
                  'context_usage_category_total_matches':True,'rewind_files':'passed','unknown_task_rejected':True,
                  'stderr':errors, 'requests_used_only_loopback':True}
        (ROOT/'sdk-subprocess-evidence.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
        print(json.dumps(result,indent=2))

asyncio.run(asyncio.wait_for(main(), 120))
