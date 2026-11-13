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
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const getBalance = async () => (await (await fetch(`${API}/balance`, { headers })).json()).balance;

const createTask = async (endpoint, body) => {
    const res = await fetch(`${API}/${endpoint}`, { method: 'POST', headers, body: JSON.stringify(body) });
    const json = await res.json();
    if (!res.ok) throw new Error(JSON.stringify(json));
    return json.result;
};

const waitTask = async (endpoint, id, start) => {
    while (true) {
        const task = await (await fetch(`${API}/${endpoint}/${id}`, { headers })).json();
        console.log(`[${((Date.now() - start) / 1000).toFixed(0)}초] ${endpoint} ${task.status} ${task.progress}%`);
        if (task.status === 'SUCCEEDED') return task;
        if (['FAILED', 'CANCELED'].includes(task.status)) throw new Error(JSON.stringify(task.task_error));
        await sleep(5000);
    }
};

const imageFiles = fs.readdirSync('images').filter((f) => /\.(png|jpe?g|webp)$/i.test(f)).sort();
console.log('이미지:', imageFiles);
if (imageFiles.length === 0) throw new Error('images 폴더에 이미지가 없습니다');

const startBalance = await getBalance();
const start = Date.now();

// 1단계: 리메시 없이 고품질 생성 (웹과 같은 방식)
const genId = await createTask('multi-image-to-3d', {
    image_urls: imageFiles.map((f) => toDataUri(path.join('images', f))),
    ai_model: 'meshy-7.1',
    enable_pbr: true,
    texture_resolution: '2k',
    should_remesh: false,
    target_formats: ['glb'],
});
const gen = await waitTask('multi-image-to-3d', genId, start);
const genTime = (Date.now() - start) / 1000;

// 2단계: 리메시 API로 15,000면으로 줄이기
const remeshId = await createTask('remesh', {
    input_task_id: genId,
    target_polycount: 15000,
    topology: 'triangle',
    target_formats: ['glb'],
});
const remesh = await waitTask('remesh', remeshId, start);

const glb = await fetch(remesh.model_urls.glb);
fs.mkdirSync('output', { recursive: true });
fs.writeFileSync('output/dog_twostep.glb', Buffer.from(await glb.arrayBuffer()));

const endBalance = await getBalance();
console.log('완료: output/dog_twostep.glb');
console.log(`생성 ${genTime.toFixed(0)}초, 전체 ${((Date.now() - start) / 1000).toFixed(0)}초`);
console.log(`생성 크레딧 ${gen.consumed_credits}, 사용 크레딧 합계 ${startBalance - endBalance}`);