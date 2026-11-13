import fs from 'node:fs';

const API = 'https://api.meshy.ai/openapi/v1';
const headers = {
    Authorization: `Bearer ${process.env.MESHY_API_KEY}`,
    'Content-Type': 'application/json',
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const list = await (await fetch(`${API}/multi-image-to-3d?page_size=1&sort_by=-created_at`, { headers })).json();
const genId = list[0].id;
console.log('원본 작업:', genId);

const createRemesh = async (body) => {
    const res = await fetch(`${API}/remesh`, { method: 'POST', headers, body: JSON.stringify({ input_task_id: genId, target_formats: ['glb'], ...body }) });
    const json = await res.json();
    if (!res.ok) throw new Error(JSON.stringify(json));
    return json.result;
};

const waitAndSave = async (id, file) => {
    while (true) {
        const t = await (await fetch(`${API}/remesh/${id}`, { headers })).json();
        if (t.status === 'SUCCEEDED') {
            const glb = await fetch(t.model_urls.glb);
            fs.writeFileSync(`output/${file}`, Buffer.from(await glb.arrayBuffer()));
            console.log('완료:', file);
            return;
        }
        if (['FAILED', 'CANCELED'].includes(t.status)) throw new Error(JSON.stringify(t.task_error));
        await sleep(5000);
    }
};

const quadId = await createRemesh({ topology: 'quad', target_polycount: 15000 });
const triId = await createRemesh({ topology: 'triangle', target_polycount: 30000 });

await Promise.all([
    waitAndSave(quadId, 'dog_quad15k.glb'),
    waitAndSave(triId, 'dog_tri30k.glb'),
]);