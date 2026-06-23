async function fetchRooms(){
  try{
    const res = await fetch('/api/rooms', { credentials: 'include' });
    if(!res.ok) throw new Error('Chargement des salles impossible');
    return await res.json();
  }catch(e){ console.error(e); return []; }
}

function renderRooms(list){
  const ul = document.getElementById('roomsList');
  const empty = document.getElementById('emptyState');
  ul.innerHTML = '';
  if (empty) empty.style.display = list.length ? 'none' : '';
  list.forEach(r=>{
    const li = document.createElement('div');
    li.className = 'col-md-4';
    li.innerHTML = `
      <div class="card h-100">
        <div class="card-body">
          <div class="d-flex justify-content-between align-items-start gap-2">
            <div>
              <h5 class="card-title mb-1"><i class="bi bi-door-open me-2 text-success"></i>${r.name}</h5>
              <div class="text-muted small"><i class="bi bi-people me-1"></i>Capacite ${r.capacity}</div>
              <div class="text-muted small"><i class="bi bi-geo-alt me-1"></i>${r.location || 'Emplacement non renseigne'}</div>
            </div>
            <button class="btn btn-sm btn-outline-danger" title="Supprimer"><i class="bi bi-trash"></i></button>
          </div>
        </div>
      </div>`;
    const del = document.createElement('button');
    del.className = 'btn btn-sm btn-outline-danger';
    del.innerHTML = '<i class="bi bi-trash"></i>';
    del.addEventListener('click', async ()=>{
      if(!confirm('Supprimer cette salle ?')) return;
      const d = await fetch(`/api/rooms/${r.id}`, { method: 'DELETE', credentials: 'include' });
      if(d.status === 204){ loadRooms(); }
      else alert('Suppression impossible');
    });
    li.querySelector('button').replaceWith(del);
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
    } else if(res.status === 403){ alert('Role admin requis'); }
    else { alert('Creation impossible'); }
  });

  async function loadRooms(){
    const list = await fetchRooms();
    renderRooms(list);
  }

  loadRooms();
});
