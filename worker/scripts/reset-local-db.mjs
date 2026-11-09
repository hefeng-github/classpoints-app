/**
 * 清空本地 D1 状态目录，让冒烟测试跑在干净库上。
 *
 * 顺序很重要：先停掉 wrangler dev，再 reset，再 dev —— 否则 miniflare 手里的句柄
 * 会指向被挪走的文件，表现是"迁移成功但表还是老的"，非常难查。
 *
 * 这里用 rename 而不是 rm：Windows 上 rmSync 在文件被占用时容易半途失败，
 * 留下一个残缺的 state 目录，比不删更糟。挪走是原子的，失败了也只是多个空目录，
 * 而且万一挪错了还能挪回来。
 */
import { renameSync, existsSync, mkdirSync } from 'node:fs';
import { execSync } from 'node:child_process';

const STATE = '.wrangler/state';

if (existsSync(STATE)) {
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const target = `${STATE}.old-${stamp}`;
  renameSync(STATE, target);
  console.log(`已把旧的本地库挪到 ${target}（确认无误后可以删掉）`);
} else {
  console.log('本地库本来就不存在，直接建新的');
}

mkdirSync('.wrangler', { recursive: true });
execSync('npx wrangler d1 migrations apply classpoints --local', { stdio: 'inherit' });
