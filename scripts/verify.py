#!/usr/bin/env python3
"""Run repository checks without starting inference or changing tracked files."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SOLUTION = 'Backend/GenProxy.sln'
UNIT = 'Backend/Tests/GenProxy.Api.UnitTests/GenProxy.Api.UnitTests.csproj'
INTEGRATION = 'Backend/Tests/GenProxy.Api.IntegrationTests/GenProxy.Api.IntegrationTests.csproj'
ALWAYS = {'ArchitectureBoundaryTests', 'LeadingPromptTruncatorPropertyTests'}
# A caller change may affect another layer. Include its boundary tests explicitly.
RULES = (
    ('Backend/Api/Services/Implementation/Services/LeadingPromptTruncator.cs', {'LeadingPromptTruncatorTests', 'LeadingPromptTruncatorPropertyTests', 'ResponseGenerationServiceTests'}),
    ('Backend/Api/Services/Implementation/Services/LlmPromptSummarizer.cs', {'LlmPromptSummarizerTests', 'PromptReductionPipelineTests', 'ResponseGenerationServiceTests'}),
    ('Backend/Api/Services/Implementation/Services/PromptReductionPipeline.cs', {'PromptReductionPipelineTests', 'PromptReductionCancellationTests', 'ResponseGenerationServiceTests'}),
    ('Backend/Api/Services/Implementation/Services/ResponseGenerationService.cs', {'ResponseGenerationServiceTests', 'PromptReductionCancellationTests'}),
    ('Backend/Api/Integrations/Implementation/Clients/', {'GrpcLlamaRuntimeClientTests', 'RuntimeClientDisposalTests', 'RuntimeTimeoutConfigurationTests'}),
    ('Backend/Api/Host/Validation/', {'ResponseCreateRequestValidatorTests', 'ResponseCreateRequestMapperTests'}),
    ('Backend/Api/Host/Endpoints/Requests/', {'ResponseCreateRequestValidatorTests', 'ResponseCreateRequestMapperTests'}),
)


def changed_paths(base=None):
    commands = [['git', 'diff', '--no-renames', '--name-only', '-z', 'HEAD'],
                ['git', 'ls-files', '--others', '--exclude-standard', '-z']]
    if base:
        commands.append(['git', 'diff', '--no-renames', '--name-only', '-z', f'{base}...HEAD'])
    paths = set()
    for command in commands:
        paths.update(filter(None, subprocess.check_output(command, cwd=ROOT).decode().split('\0')))
    return sorted(paths)


def select_tests(paths):
    classes = set(ALWAYS)
    integration = False
    tooling = False
    full = not paths
    for path in paths:
        if path.startswith('Backend/Tools/'):
            tooling = True
            continue
        if path.startswith('Backend/Tests/GenProxy.Api.UnitTests/') and path.endswith('Tests.cs'):
            classes.add(Path(path).stem)
            continue
        if path.startswith('Backend/Tests/GenProxy.Api.IntegrationTests/'):
            integration = True
            continue
        matched = False
        for prefix, affected in RULES:
            if path.startswith(prefix):
                classes.update(affected)
                matched = True
                break
        if matched:
            integration = True
        elif path.startswith('docs/') or path in ('README.md', 'AGENTS.md'):
            continue
        else:
            # Shared contracts, composition, build settings and unknown files are broad changes.
            full = True
    return {'unit_filter': None if full else '|'.join(f'FullyQualifiedName~{name}' for name in sorted(classes)),
            'integration': full or integration, 'tooling': full or tooling}


def discovered_count(paths):
    total = 0
    for path in paths:
        counters = ET.parse(path).getroot().find('.//{*}Counters')
        if counters is None:
            raise ValueError(f'{path}: missing test counters')
        total += int(counters.attrib['total'])
    return total


def is_test_project(project):
    document = ET.parse(project).getroot()
    declared = document.findtext('.//IsTestProject')
    if declared is not None:
        return declared.strip().lower() == 'true'
    return any(reference.attrib.get('Include') == 'Microsoft.NET.Test.Sdk'
               for reference in document.findall('.//PackageReference'))


def run(command):
    print('+ ' + ' '.join(command), flush=True)
    subprocess.run(command, cwd=ROOT, check=True)


def test_project(project, test_filter=None):
    with tempfile.TemporaryDirectory(prefix='gen-proxy-tests-') as directory:
        command = ['dotnet', 'test', project, '-c', 'Release', '--no-build', '--no-restore',
                   '-p:CollectCoverage=false', '--logger', 'trx', '--results-directory', directory]
        if test_filter:
            command += ['--filter', test_filter]
        command += ['--', 'RunConfiguration.TreatNoTestsAsError=true']
        run(command)
        reports = list(Path(directory).glob('*.trx'))
        if not reports or discovered_count(reports) == 0:
            raise RuntimeError(f'{project}: no tests discovered')
        print(f'{project}: {discovered_count(reports)} tests discovered', flush=True)


def verify_tools():
    for project in sorted((ROOT / 'Backend/Tools').glob('*/*.csproj')):
        run(['dotnet', 'build', str(project.relative_to(ROOT)), '-c', 'Release', '--disable-build-servers'])
        if is_test_project(project):
            test_project(str(project.relative_to(ROOT)))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fast', action='store_true')
    parser.add_argument('--base', help='Include committed changes since this Git revision')
    parser.add_argument('--plan', action='store_true', help='Print selection without running checks')
    args = parser.parse_args()
    paths = changed_paths(args.base)
    plan = select_tests(paths) if args.fast else {'unit_filter': None, 'integration': True, 'tooling': True}
    if args.plan:
        print(json.dumps({'paths': paths, **plan}, indent=2))
        return
    # PLATFORM exported by another project's Makefile must not become an MSBuild platform.
    os.environ.pop('PLATFORM', None)
    run(['python3', '-m', 'unittest', 'discover', '-s', 'scripts/tests', '-v'])
    run(['dotnet', 'restore', SOLUTION])
    run(['dotnet', 'build', SOLUTION, '-c', 'Release', '--no-restore', '--disable-build-servers'])
    run(['dotnet', 'format', 'style', SOLUTION, '--verify-no-changes', '--diagnostics', 'IDE0011', '--no-restore'])
    test_project(UNIT, plan['unit_filter'])
    if plan['integration']:
        test_project(INTEGRATION)
    if plan['tooling']:
        verify_tools()
    run(['git', 'diff', '--check'])


if __name__ == '__main__':
    try:
        main()
    except (subprocess.CalledProcessError, RuntimeError, ValueError) as error:
        raise SystemExit(str(error))
