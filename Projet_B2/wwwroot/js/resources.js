document.addEventListener('DOMContentLoaded', async () => {
  const grid = document.getElementById('resourcesGrid');
  const emptyMsg = document.getElementById('emptyMsg');
  const adminSection = document.getElementById('adminSection');
  const form = document.getElementById('addResourceForm');
  const msg = document.getElementById('addMsg');

  // check if admin to show add form
  const meRes = await fetch('/api/me', { credentials: 'include' });
  if (meRes.ok) {
    const me = await meRes.json();
    if (me.role === 'Admin') adminSection.style.display = '';
  }

  const typeIcons = { 'Équipement': '🔧', 'Mobilier': '🪑', 'Informatique': '💻', 'Autre': '📦' };

  async function loadResources() {
    const res = await fetch('/api/resources', { credentials: 'include' });
    if (!res.ok) { grid.innerHTML = '<p class="text-danger">Erreur de chargement.</p>'; return; }
    const items = await res.json();
    grid.innerHTML = '';
    emptyMsg.style.display = items.length ? 'none' : '';
    items.forEach(r => {
      const col = document.createElement('div');
      col.className = 'col-md-4 col-sm-6 mb-3';
      const icon = typeIcons[r.type] || '📦';
      col.innerHTML = `
        <div class="card h-100 shadow-sm">
          <div class="card-body">
            <div class="d-flex justify-content-between align-items-start">
              <div>
                <span class="fs-4 me-2">${icon}</span>
                <strong>${r.name}</strong>
              </div>
              <span class="badge bg-secondary">${r.type || 'Autre'}</span>
            </div>
            <div class="mt-2 text-muted small">
              ${r.capacity > 0 ? `<div>Capacité : ${r.capacity}</div>` : ''}
              ${r.price > 0 ? `<div>Prix : ${r.price.toFixed(2)} €/h</div>` : '<div>Gratuit</div>'}
            </div>
          </div>
          <div class="card-footer d-flex justify-content-end admin-only" style="display:none!important">
            <button class="btn btn-sm btn-outline-danger del-btn" data-id="${r.id}">Supprimer</button>
          </div>
        </div>`;
      col.querySelector('.del-btn').addEventListener('click', async () => {
        if (!confirm(`Supprimer "${r.name}" ?`)) return;
        const d = await fetch(`/api/resources/${r.id}`, { method: 'DELETE', credentials: 'include' });
        if (d.ok || d.status === 204) col.remove();
        else alert('Erreur lors de la suppression');
      });
      grid.appendChild(col);
    });

    // show delete buttons for admins
    if (adminSection.style.display !== 'none') {
      document.querySelectorAll('.card-footer.admin-only').forEach(el => el.style.removeProperty('display'));
    }
  }

  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    const fd = new FormData(form);
    const payload = {
      name: fd.get('name'),
      type: fd.get('type'),
      capacity: parseInt(fd.get('capacity') || '0'),
      price: parseFloat(fd.get('price') || '0')
    };
    const res = await fetch('/api/resources', {
      method: 'POST', credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    if (res.status === 201) {
      msg.textContent = 'Ressource ajoutée.'; msg.style.color = 'green';
      form.reset(); loadResources();
    } else {
      const j = await res.json().catch(() => null);
      msg.textContent = j?.error || 'Erreur.'; msg.style.color = 'red';
    }
  });

  loadResources();
});
