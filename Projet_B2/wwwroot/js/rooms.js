async function fetchRooms(){
  try{
    const res = await fetch('/api/rooms', { credentials: 'include' });
    if(!res.ok) throw new Error('Failed to load rooms');
    return await res.json();
  }catch(e){ console.error(e); return []; }
}

function renderRooms(list){
  const ul = document.getElementById('roomsList');
  ul.innerHTML = '';
  list.forEach(r=>{
    const li = document.createElement('li');
    li.innerHTML = `<strong>${r.name}</strong> (cap ${r.capacity}) <span class="muted">${r.location || ''}</span> `;
    const del = document.createElement('button');
    del.textContent = 'Delete';
    del.addEventListener('click', async ()=>{
      if(!confirm('Delete this room?')) return;
      const d = await fetch(`/api/rooms/${r.id}`, { method: 'DELETE', credentials: 'include' });
      if(d.status === 204){ li.remove(); }
      else alert('Failed to delete');
    });
    li.appendChild(del);
    ul.appendChild(li);
  });
}

document.addEventListener('DOMContentLoaded', ()=>{
  const form = document.getElementById('addRoomForm');
  form.addEventListener('submit', async (e)=>{
    e.preventDefault();
    const fd = new FormData(form);
    const payload = { name: fd.get('name'), capacity: parseInt(fd.get('capacity')||'0'), location: fd.get('location') };
    const res = await fetch('/api/rooms', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify(payload), credentials: 'include' });
    if(res.status === 201){
      form.reset();
      loadRooms();
    } else if(res.status === 403){ alert('Admin only'); }
    else { alert('Failed to create'); }
  });

  async function loadRooms(){
    const list = await fetchRooms();
    renderRooms(list);
  }

  loadRooms();
});
