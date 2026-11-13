import fs from 'node:fs';
import path from 'node:path';

const API = 'https://api.meshy.ai/openapi/v1';
const headers = {
    Authorization: `Bearer ${process.env.MESHY_API_KEY}`,
    'Content-Type': 'application/json',
};

const toDataUri = (file) => {
    const ext = path.extname(file).slice(1).replace('jpg', 'jpeg');
    return `data:image/${ext};base64,${fs.readFileSync(file).toString('base64')}`;
};

const getBalance = async () => (await fetch(`${API}/balance`, { headers })).json();
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const images = fs.readdirSync('images').sort().map((f) => toDataUri(path.join('images', f)));
console.log('잔액(시작):', await getBalance());

const start = Date.now();
const createRes = await fetch(`${API}/multi-image-to-3d`, {
    method: 'POST',
    headers,
    body: JSON.stringify({
        image_urls: images,
        ai_model: 'meshy-7.1',
        enable_pbr: true,
        texture_resolution: '2k',
        should_remesh: true,
        target_polycount: 15000,
        target_formats: ['glb'],
    }),
});
const created = await createRes.json();
if (!createRes.ok) throw new Error(JSON.stringify(created));
const taskId = created.result;
console.log('작업 ID:', taskId);

let task;
while (true) {
    task = await (await fetch(`${API}/multi-image-to-3d/${taskId}`, { headers })).json();
    console.log(`[${((Date.now() - start) / 1000).toFixed(0)}초] ${task.status} ${task.progress}%`);
    if (['SUCCEEDED', 'FAILED', 'CANCELED'].includes(task.status)) break;
    await sleep(5000);
}
if (task.status !== 'SUCCEEDED') throw new Error(JSON.stringify(task.task_error));

const glb = await fetch(task.model_urls.glb);
fs.mkdirSync('output', { recursive: true });
fs.writeFileSync('output/dog.glb', Buffer.from(await glb.arrayBuffer()));

console.log('완료: output/dog.glb');
console.log('총 소요 시간(초):', ((Date.now() - start) / 1000).toFixed(0));
console.log('사용 크레딧:', task.consumed_credits);
console.log('잔액(종료):', await getBalance());