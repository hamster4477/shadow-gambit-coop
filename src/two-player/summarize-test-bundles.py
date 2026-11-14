"""Read two support ZIPs without extracting them; produce evidence and next checks."""
import argparse
import importlib.util
import json
from pathlib import Path
import zipfile

MAX_LOG = 64 * 1024 * 1024


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


guards = module('support_guard_comparison', 'compare-guard-logs.py')
general = module('support_log_analysis', 'analyze-coop-logs.py')


def read_bundle(path):
    with zipfile.ZipFile(path) as archive:
        def bounded(name, limit):
            entries = [entry for entry in archive.infolist() if entry.filename == name]
            if len(entries) != 1 or entries[0].file_size > limit:
                raise ValueError(f'{name}: 缺失、重名或超过大小限制')
            with archive.open(entries[0]) as file:
                data = file.read(limit + 1)
            if len(data) > limit:
                raise ValueError(f'{name}: 超过大小限制')
            return data.decode('utf-8-sig')
        check = json.loads(bounded('check.json', 256 * 1024))
        if not isinstance(check, dict) or check.get('schema') != 1:
            raise ValueError('不支持的检查报告格式')
        return check, bounded('coop.log', MAX_LOG)


def summarize(host, guest):
    hc, hl = host
    gc, gl = guest
    guard_report = guards.compare(hl, gl)
    actions = []
    for role, check in [('Host', hc), ('Guest', gc)]:
        if check.get('role') not in (role, 'Unknown'):
            actions.append(f'{role} 参数收到另一角色的结果包，请先核对是否放反。')
        if check.get('installedMatchesPreparedBuild') is not True:
            actions.append(f'{role} 插件未确认与准备的版本一致，先核对安装文件。')
        expected = check.get('expectedVersion')
        if not expected or check.get('lastLoggedVersion') != expected:
            actions.append(f'{role} 最近启动版本未确认与准备版本一致；安装后重启游戏再采集。')
        settings = check.get('settings', {})
        markers = check.get('markers', {})
        if not isinstance(settings, dict) or not isinstance(markers, dict):
            raise ValueError('检查报告 settings/markers 格式错误')
        if settings.get('guardObservation') == 'false':
            actions.append(f'{role} 守卫观测开关关闭，开启后重新启动采集。')
        if markers.get('legacyOrFallback'):
            actions.append(f'{role} 存在旧按键或输入钩子回退记录，先检查原版输入捕获。')
        if markers.get('diagnosticFailure') or markers.get('diagnosticLimit'):
            actions.append(f'{role} 观测曾停止或达到日志上限；这份采样可能不完整。')
    if hc.get('installedDllSha256') and gc.get('installedDllSha256') and hc['installedDllSha256'] != gc['installedDllSha256']:
        actions.append('两端插件文件哈希不同，先统一安装包。')
    comparisons = guard_report['comparisons']
    if not comparisons:
        actions.append('没有可对应的观测批次：检查是否为同一轮、是否成功 F7/F6 联机、是否启用诊断。')
    elif any(not item['mapping']['matching_inventory'] for item in comparisons):
        actions.append('至少一轮敌人清单不完整或不一致，先处理 ID 映射，不能直接进入 AI 接管实验。')
    paired = sum(item['samples'].get('paired_samples', 0) for item in comparisons)
    health = sum(item['samples'].get('health_different_samples', 0) for item in comparisons)
    alert = sum(item['samples'].get('alert_different_samples', 0) for item in comparisons)
    if comparisons and paired == 0:
        actions.append('已有批次但没有可信的守卫配对样本：检查 F4 目标、连接、暂停和样本缺失原因。')
    if health:
        actions.append('已观察到两端生命状态不同，结合对应轮次画面检查击晕/击杀结果传播。')
    if alert:
        actions.append('已观察到两端警戒状态不同，结合巡逻或警报轮次检查检测和警报来源。')
    if paired and not health and not alert:
        actions.append('这批抽样未发现生命/警戒差异；仍需结合画面，不能据此判定完整敌人同步成功。')
    return {'schema': 1,
            'summary': {'paired_guard_samples': paired, 'health_different_samples': health,
                        'alert_different_samples': alert},
            'next_checks': actions,
            'command_evidence': general.report_for(hl, gl),
            'guard_evidence': guard_report}


def main():
    # Redirected Windows consoles may not support Chinese; the JSON stays UTF-8.
    import sys
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, 'reconfigure'):
            stream.reconfigure(errors='backslashreplace')
    parser = argparse.ArgumentParser(description='直接汇总双方一键收集的结果 ZIP')
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--guest', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        result = summarize(read_bundle(args.host), read_bundle(args.guest))
        args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False) + '\n', encoding='utf-8')
        print(f"配对守卫样本：{result['summary']['paired_guard_samples']}")
        for action in result['next_checks']:
            print('- ' + action)
        print('完整报告：' + str(args.output))
    except (OSError, ValueError, KeyError, zipfile.BadZipFile) as error:
        parser.exit(1, f'汇总失败：{error}\n')


if __name__ == '__main__':
    main()
