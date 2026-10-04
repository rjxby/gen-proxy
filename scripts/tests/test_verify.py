import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('verify', Path(__file__).parents[1] / 'verify.py')
verify = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verify)


class SelectionTests(unittest.TestCase):
    def test_local_service_edit_includes_callers_and_http_boundary(self):
        plan = verify.select_tests(['Backend/Api/Services/Implementation/Services/LeadingPromptTruncator.cs'])
        self.assertIn('LeadingPromptTruncatorTests', plan['unit_filter'])
        self.assertIn('ResponseGenerationServiceTests', plan['unit_filter'])
        self.assertIn('ArchitectureBoundaryTests', plan['unit_filter'])
        self.assertTrue(plan['integration'])

    def test_shared_and_unknown_changes_fall_back_to_all_tests(self):
        for path in ['Backend/Api/Services/Contracts/Models/ResponseCreateCommand.cs',
                     '.editorconfig', 'Backend/Tests/GenProxy.Api.UnitTests/Testing/NewHelper.cs', 'unknown.txt']:
            with self.subTest(path=path):
                plan = verify.select_tests([path])
                self.assertIsNone(plan['unit_filter'])
                self.assertTrue(plan['integration'])
                self.assertTrue(plan['tooling'])

    def test_tooling_stays_outside_api_solution(self):
        plan = verify.select_tests(['Backend/Tools/GenProxy.StackRunner/Program.cs'])
        self.assertTrue(plan['tooling'])
        self.assertIn('ArchitectureBoundaryTests', plan['unit_filter'])

    def test_no_changes_cannot_silently_skip_verification(self):
        self.assertIsNone(verify.select_tests([])['unit_filter'])

    def test_zero_and_missing_test_counters_are_not_a_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / 'empty.trx'
            report.write_text('<TestRun xmlns="urn:trx"><ResultSummary><Counters total="0"/></ResultSummary></TestRun>')
            self.assertEqual(0, verify.discovered_count([report]))
            report.write_text('<TestRun/>')
            with self.assertRaises(ValueError):
                verify.discovered_count([report])

    def test_positive_test_counts_use_namespace_independent_parsing(self):
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / 'results.trx'
            report.write_text('<TestRun xmlns="urn:trx"><ResultSummary><Counters total="12"/></ResultSummary></TestRun>')
            self.assertEqual(12, verify.discovered_count([report]))


class ToolProjectTests(unittest.TestCase):
    def test_explicit_test_flag_and_sdk_are_detected(self):
        with tempfile.TemporaryDirectory() as directory:
            project = Path(directory) / 'Tool.csproj'
            for source, expected in [
                ('<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>', True),
                ('<Project><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk"/></ItemGroup></Project>', True),
                ('<Project><PropertyGroup><IsTestProject>false</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk"/></ItemGroup></Project>', False),
                ('<Project/>', False),
            ]:
                project.write_text(source)
                self.assertEqual(expected, verify.is_test_project(project))


class ToolExecutionTests(unittest.TestCase):
    def test_verification_builds_and_tests_only_discovered_projects(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            tool = root / 'Backend/Tools/Example/Example.csproj'
            tests = root / 'Backend/Tools/Example.Tests/Example.Tests.csproj'
            for project, source in [
                (tool, '<Project/>'),
                (tests, '<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>'),
            ]:
                project.parent.mkdir(parents=True)
                project.write_text(source)
            with patch.object(verify, 'ROOT', root), patch.object(verify, 'run') as run, \
                 patch.object(verify, 'test_project') as run_tests:
                verify.verify_tools()
                self.assertEqual([
                    ['dotnet', 'build', 'Backend/Tools/Example/Example.csproj', '-c', 'Release', '--disable-build-servers'],
                    ['dotnet', 'build', 'Backend/Tools/Example.Tests/Example.Tests.csproj', '-c', 'Release', '--disable-build-servers'],
                ], [call.args[0] for call in run.call_args_list])
                run_tests.assert_called_once_with('Backend/Tools/Example.Tests/Example.Tests.csproj')

    def test_failing_tool_tests_fail_verification(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            project = root / 'Backend/Tools/Example.Tests/Example.Tests.csproj'
            project.parent.mkdir(parents=True)
            project.write_text('<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>')
            with patch.object(verify, 'ROOT', root), patch.object(verify, 'run'), \
                 patch.object(verify, 'test_project', side_effect=RuntimeError('tool regression failed')) as run_tests:
                with self.assertRaisesRegex(RuntimeError, 'tool regression failed'):
                    verify.verify_tools()
                run_tests.assert_called_once_with('Backend/Tools/Example.Tests/Example.Tests.csproj')
