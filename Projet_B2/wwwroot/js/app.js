// FONCTIONNALITE: fonctions communes du site, navigation, login, espaces et equipements.
async function postJson(url, data) {
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(data),
    credentials: 'include'
  });
  return res;
}

// FONCTIONNALITE: affichage du menu admin selon le role connecte.
function applyAdminMenu(adminMenu, role) {
  if (!adminMenu) return;
  const items = adminMenu.querySelectorAll('[data-roles]');
  let visibleCount = 0;
  items.forEach(li => {
    const allowed = li.dataset.roles.split(',');
    const show = allowed.includes(role);
    li.style.display = show ? '' : 'none';
    if (show) visibleCount++;
  });
  adminMenu.style.display = visibleCount > 0 ? '' : 'none';
  const label = document.getElementById('adminMenuLabel');
  if (label) {
    label.textContent = role === 'Comptabilite' ? 'Comptabilit\u00e9' : (role === 'Accueil' ? 'Accueil' : 'Admin');
  }
}

// FONCTIONNALITE: mise a jour de la barre de navigation apres lecture de /api/me.
async function refreshUser() {
  try {
    const res = await fetch('/api/me', { credentials: 'include' });
    const loginLink = document.getElementById('loginLink');
    const signupLink = document.getElementById('signupLink');
    const logout = document.getElementById('logout');
    const username = document.getElementById('username');
    const userChip = document.getElementById('userChip');
    const userRole = document.getElementById('userRole');
    const adminMenu = document.getElementById('adminMenu');
    if (!loginLink || !logout || !username) return;
    if (res.ok) {
      const data = await res.json();
      loginLink.style.display = 'none';
      if (signupLink) signupLink.style.display = 'none';
      logout.style.display = 'inline-flex';
      username.textContent = data.user || '';
      if (userRole) userRole.textContent = data.role || 'User';
      if (userChip) userChip.style.display = 'inline-flex';
      applyAdminMenu(adminMenu, data.role || '');
    } else {
      loginLink.style.display = 'inline-flex';
      if (signupLink) signupLink.style.display = 'inline-flex';
      logout.style.display = 'none';
      username.textContent = '';
      if (userChip) userChip.style.display = 'none';
      if (adminMenu) adminMenu.style.display = 'none';
    }
  } catch {
    // Ignore transient network errors on shared layout load.
  }
}

document.addEventListener('DOMContentLoaded', () => {
  // FONCTIONNALITE: formulaire de connexion.
  const loginForm = document.getElementById('loginForm');
  if (loginForm) {
    loginForm.addEventListener('submit', async (e) => {
      e.preventDefault();
      const fd = new FormData(loginForm);
      const res = await postJson('/api/login', { username: fd.get('username'), password: fd.get('password') });
      if (res.ok) location.href = '/Spaces';
      else document.getElementById('msg').textContent = 'Connexion impossible';
    });
  }

  // FONCTIONNALITE: formulaire d'ajout d'un espace depuis la page Espaces.
  const addForm = document.getElementById('addForm');
  if (addForm) {
    addForm.addEventListener('submit', async (e) => {
      e.preventDefault();
      const fd = new FormData(addForm);
      const name = fd.get('name');
      const capacity = parseInt(fd.get('capacity')) || 0;
      const pricePerHour = parseFloat(fd.get('pricePerHour')) || 0;
      const type = fd.get('type') || 'Nomad';
      const res = await postJson('/api/spaces', { name, capacity, pricePerHour, type });
      if (res.ok) {
        addForm.reset();
        loadList();
      } else {
        alert('Ajout impossible');
      }
    });
  }

  // FONCTIONNALITE: modal d'ajout des equipements d'un espace.
  const resAddForm = document.getElementById('resAddForm');
  if (resAddForm) {
    const equipmentSelect = document.getElementById('equipmentSelect');
    const equipmentType = document.getElementById('equipmentType');
    if (equipmentSelect && equipmentType) {
      equipmentSelect.addEventListener('change', () => {
        const selected = equipmentSelect.options[equipmentSelect.selectedIndex];
        equipmentType.value = selected ? (selected.dataset.type || '') : '';
      });
    }

    resAddForm.addEventListener('submit', async (e) => {
      e.preventDefault();
      const fd = new FormData(resAddForm);
      const spaceId = fd.get('spaceId');
      const payload = {
        name: fd.get('name'),
        type: fd.get('type'),
        quantity: parseInt(fd.get('quantity')) || 1
      };
      const r = await fetch(`/api/spaces/${spaceId}/resources`, {
        method: 'POST',
        headers: {'Content-Type':'application/json'},
        credentials:'include',
        body: JSON.stringify(payload)
      });
      if (r.ok) {
        resAddForm.reset();
        renderResources(spaceId);
      } else {
        alert('Ajout de ressource impossible');
      }
    });
  }

  const logout = document.getElementById('logout');
  if (logout) {
    logout.addEventListener('click', async (e) => {
      e.preventDefault();
      await postJson('/api/logout', {});
      window.location.href = '/Login';
    });
  }

  if (document.getElementById('list')) loadList();
  refreshUser().then(() => {
    const path = window.location.pathname.toLowerCase();
    const anonymous = ['/login', '/signup'];
    if (!anonymous.includes(path) && document.getElementById('loginForm') == null && document.getElementById('signupForm') == null) {
      fetch('/api/me', { credentials: 'include' }).then(r => {
        if (!r.ok) window.location.href = '/Login';
      }).catch(() => { /* ignore */ });
    }
  });
});

function typeBadgeHtml(t){
  const map = { Nomad: 'bg-info', Office: 'bg-primary', Meeting: 'bg-warning text-dark', Conference: 'bg-danger' };
  return t ? `<span class="badge ${map[t] || 'bg-secondary'} me-2">${t}</span>` : '';
}

function formatEuro(value){
  return `${(Number(value) || 0).toLocaleString('fr-FR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} \u20ac`;
}

// FONCTIONNALITE: affichage de la liste des espaces sous forme de cartes.
async function loadList() {
  const res = await fetch('/api/spaces', { credentials: 'include' });
  if (res.status === 401) { location.href = '/Login'; return; }
  const items = await res.json();
  const ul = document.getElementById('list');
  ul.innerHTML = '';
  const empty = document.getElementById('emptyState');
  if (empty) empty.style.display = items.length ? 'none' : '';
  for (const it of items) {
    const col = document.createElement('div');
    col.className = 'col-md-4';
    const price = (it.pricePerHour != null) ? `${formatEuro(it.pricePerHour)}/h` : '-';
    col.innerHTML = `
      <div class="card space-card h-100">
        <div class="card-body">
          <div class="d-flex justify-content-between align-items-start gap-2">
            <h5 class="card-title mb-1">${typeBadgeHtml(it.type)}${it.name}</h5>
            <span class="badge bg-light text-dark border">${price}</span>
          </div>
          <p class="text-muted small mb-3"><i class="bi bi-people me-1"></i>Capacit\u00e9 : <strong>${it.capacity}</strong></p>
          <div class="d-flex gap-2 table-actions">
            <button class="btn btn-sm btn-outline-secondary res-btn" data-id="${it.id}" data-name="${it.name}" title="\u00c9quipements"><i class="bi bi-tools"></i></button>
            <button class="btn btn-sm btn-outline-danger del-btn" data-id="${it.id}" title="Supprimer"><i class="bi bi-trash"></i></button>
          </div>
        </div>
      </div>`;
    col.querySelector('.del-btn').addEventListener('click', async () => {
      if (!confirm(`Supprimer "${it.name}" ?`)) return;
      const r = await fetch(`/api/spaces/${it.id}`, { method: 'DELETE', credentials: 'include' });
      if (r.ok) loadList();
    });
    col.querySelector('.res-btn').addEventListener('click', () => {
      document.querySelector('#resAddForm input[name=spaceId]').value = it.id;
      document.getElementById('resTitle').textContent = '\u00b7 ' + it.name;
      renderResources(it.id);
      new bootstrap.Modal(document.getElementById('resModal')).show();
    });
    ul.appendChild(col);
  }
}

// FONCTIONNALITE: affichage/suppression des equipements lies a un espace.
async function renderResources(spaceId){
  const list = document.getElementById('resList');
  list.innerHTML = '<li class="list-group-item text-muted">Chargement...</li>';
  const res = await fetch(`/api/spaces/${spaceId}/resources`, { credentials: 'include' });
  if (!res.ok) { list.innerHTML = '<li class="list-group-item text-danger">Chargement impossible</li>'; return; }
  const items = await res.json();
  if (!items.length) { list.innerHTML = '<li class="list-group-item text-muted">Aucun \u00e9quipement pour le moment.</li>'; return; }
  list.innerHTML = '';
  for (const r of items){
    const li = document.createElement('li');
    li.className = 'list-group-item d-flex justify-content-between align-items-center';
    li.innerHTML = `<div><strong>${r.name}</strong> <span class="text-muted small">${r.type ? r.type + ' \u00b7 ' : ''}x ${r.quantity}</span></div>
      <button class="btn btn-sm btn-link text-danger"><i class="bi bi-x-circle"></i></button>`;
    li.querySelector('button').addEventListener('click', async () => {
      const d = await fetch(`/api/resources/${r.id}`, { method: 'DELETE', credentials: 'include' });
      if (d.ok) renderResources(spaceId);
    });
    list.appendChild(li);
  }
}
