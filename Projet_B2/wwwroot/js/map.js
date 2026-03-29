async function fetchSpaces(){
  try{
    // Prefer rooms if present
    let res = await fetch('/api/rooms', { credentials: 'include' });
    if(res.ok){
      const rooms = await res.json();
      if(Array.isArray(rooms) && rooms.length) return rooms.map(r=>({ id: r.id, name: r.name, capacity: r.capacity }));
    }
    res = await fetch('/api/spaces', { credentials: 'include' });
    if(!res.ok) throw new Error('Failed to load');
    return await res.json();
  }catch(e){
    console.error(e);
    return [];
  }
}

function renderMap(spaces){
  const container = document.getElementById('mapContainer');
  container.innerHTML = '';
  const svgNS = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(svgNS,'svg');
  svg.setAttribute('class','space-map');
  svg.setAttribute('viewBox','0 0 1000 420');

  const cols = 4;
  const rows = Math.ceil(spaces.length / cols) || 1;
  const pad = 20;
  const cellW = (1000 - (pad*(cols+1))) / cols;
  const cellH = (360 - (pad*(rows+1))) / rows;

  spaces.forEach((s, i)=>{
    const col = i % cols;
    const row = Math.floor(i/cols);
    const x = pad + col*(cellW + pad);
    const y = pad + row*(cellH + pad);

    const rect = document.createElementNS(svgNS,'rect');
    rect.setAttribute('x', x);
    rect.setAttribute('y', y);
    rect.setAttribute('rx', 8);
    rect.setAttribute('ry', 8);
    rect.setAttribute('width', cellW);
    rect.setAttribute('height', cellH);
    rect.classList.add('space-rect');
    if(s.capacity && s.capacity < 2) rect.classList.add('occupied');
    rect.addEventListener('click', ()=>{ selectSpace(s.id); });
    const title = document.createElementNS(svgNS,'title');
    title.textContent = `${s.name} — capacity ${s.capacity}`;
    rect.appendChild(title);
    svg.appendChild(rect);
    rect.dataset.spaceId = s.id;

    const name = document.createElementNS(svgNS,'text');
    name.setAttribute('x', x + 12);
    name.setAttribute('y', y + 28);
    name.setAttribute('class','space-label');
    name.textContent = s.name;
    svg.appendChild(name);

    const cap = document.createElementNS(svgNS,'text');
    cap.setAttribute('x', x + 12);
    cap.setAttribute('y', y + 48);
    cap.setAttribute('class','space-cap');
    cap.textContent = `Capacity: ${s.capacity}`;
    svg.appendChild(cap);
  });

  container.appendChild(svg);
  document.getElementById('count').textContent = `${spaces.length} spaces`;
}

function clearSelection(){
  document.querySelectorAll('.space-rect.selected').forEach(el=>el.classList.remove('selected'));
  document.querySelectorAll('.space-list li.selected').forEach(el=>el.classList.remove('selected'));
}

function selectSpace(id){
  clearSelection();
  const rect = document.querySelector(`.space-rect[data-space-id='${id}']`);
  const li = document.querySelector(`#spaceList li[data-space-id='${id}']`);
  if(rect) rect.classList.add('selected');
  if(li) li.classList.add('selected');
  const spaces = window.__spacesCache || [];
  const s = spaces.find(x=>x.id==id);
  const detail = document.getElementById('detailPanel');
  if(s && detail){ detail.innerHTML = `<strong>${s.name}</strong><div>Capacity: ${s.capacity}</div>`; }
}

async function loadAndRender(){
  const spaces = await fetchSpaces();
  window.__spacesCache = spaces;
  renderList(spaces);
  renderMap(spaces);
}

function renderList(spaces){
  const ul = document.getElementById('spaceList');
  ul.innerHTML = '';
  spaces.forEach(s=>{
    const li = document.createElement('li');
    li.textContent = `${s.name} (cap ${s.capacity})`;
    li.dataset.spaceId = s.id;
    li.dataset.name = s.name;
    li.addEventListener('mouseenter', ()=>{ const r = document.querySelector(`.space-rect[data-space-id='${s.id}']`); if(r) r.classList.add('selected'); const detail = document.getElementById('detailPanel'); if(detail) detail.innerHTML = `<strong>${s.name}</strong><div>Capacity: ${s.capacity}</div>`; });
    li.addEventListener('mouseleave', ()=>{ clearSelection(); });
    li.addEventListener('click', ()=> selectSpace(s.id));
    ul.appendChild(li);
  });
}

document.addEventListener('DOMContentLoaded', ()=>{
  document.getElementById('refresh').addEventListener('click', loadAndRender);
  const search = document.getElementById('search');
  if(search){ search.addEventListener('input', (e)=>{ const q = e.target.value.toLowerCase(); document.querySelectorAll('#spaceList li').forEach(li=>{ const name = li.dataset.name.toLowerCase(); li.style.display = name.includes(q) ? '' : 'none'; }); }); }
  loadAndRender();
});
