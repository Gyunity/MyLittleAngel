import fs from 'node:fs';

const API = 'https://api.meshy.ai/openapi/v1';
const headers = {
  Authorization: `Bearer ${process.env.MESHY_API_KEY}`,
  'Content-Type': 'application/json',
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const input = 'output/dog_quad15k.glb';
const modelUri = `data:model/gltf-binary;base64,${fs.readFileSync(input).toString('base64')}`;

const start = Date.now();
const res = await fetch(`${API}/rigging`, {
  method: 'POST',
  headers,
    body: JSON.stringify({
        model_url: modelUri,
        animation_type: 'quadruped',
        height_meters: 0.3
    }),
});
const created = await res.json();
if (!res.ok) throw new Error(JSON.stringify(created));
const id = created.result;
console.log('리깅 작업 ID:', id);

let task;
while (true) {
  task = await (await fetch(`${API}/rigging/${id}`, { headers })).json();
  console.log(`[${((Date.now() - start) / 1000).toFixed(0)}초] ${task.status} ${task.progress}%`);
  if (task.status === 'SUCCEEDED') break;
  if (['FAILED', 'CANCELED'].includes(task.status)) throw new Error(JSON.stringify(task.task_error));
  await sleep(5000);
}

const save = async (url, file) => {
  if (!url) return console.log('없음:', file);
  fs.writeFileSync(`output/${file}`, Buffer.from(await (await fetch(url)).arrayBuffer()));
  console.log('저장:', file);
};

await save(task.result.rigged_character_glb_url, 'dog_rigged_q.glb');
await save(task.result.basic_animations?.walking_glb_url, 'dog_walk_q.glb');
await save(task.result.basic_animations?.running_glb_url, 'dog_run_q.glb');

console.log('소요 시간(초):', ((Date.now() - start) / 1000).toFixed(0));
console.log('사용 크레딧:', task.consumed_credits);