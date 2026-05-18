document.addEventListener('DOMContentLoaded', () => {
  const tbody = document.getElementById('usersList');
  const form = document.getElementById('addUserForm');
  const msg = document.getElementById('addMsg');

  async function loadUsers() {
    const res = await fetch('/api/users', { credentials: 'include' });
    if (res.status === 403) { tbody.innerHTML = '<tr><td colspan="6" class="text-danger">Accès réservé aux administrateurs.</td></tr>'; return; }
    if (!res.ok) { tbody.innerHTML = '<tr><td colspan="6">Erreur de chargement.</td></tr>'; return; }
    const users = await res.json();
    tbody.innerHTML = '';
    users.forEach(u => {
      const tr = document.createElement('tr');
      const roleSelect = `<select class="form-select form-select-sm" data-id="${u.id}" style="width:120px">
        <option value="User"${u.role === 'User' ? ' selected' : ''}>Utilisateur</option>
        <option value="Admin"${u.role === 'Admin' ? ' selected' : ''}>Admin</option>
      </select>`;
      tr.innerHTML = `<td>${u.id}</td><td>${u.name}</td><td>${u.lastName}</td><td>${u.email}</td><td>${roleSelect}</td><td></td>`;

      // role change
      tr.querySelector('select').addEventListener('change', async function() {
        const r = await fetch(`/api/users/${u.id}/role`, {
          method: 'PATCH', credentials: 'include',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ role: this.value })
        });
        if (!r.ok) { alert('Erreur lors du changement de rôle'); this.value = u.role; }
        else u.role = this.value;
      });

      // delete button
      const delBtn = document.createElement('button');
      delBtn.textContent = 'Supprimer';
      delBtn.className = 'btn btn-sm btn-outline-danger';
      delBtn.addEventListener('click', async () => {
        if (!confirm(`Supprimer l'utilisateur ${u.email} ?`)) return;
        const r = await fetch(`/api/users/${u.id}`, { method: 'DELETE', credentials: 'include' });
        if (r.ok || r.status === 204) tr.remove();
        else alert('Erreur lors de la suppression');
      });
      tr.lastElementChild.appendChild(delBtn);
      tbody.appendChild(tr);
    });
    if (!users.length) tbody.innerHTML = '<tr><td colspan="6" class="text-muted">Aucun utilisateur.</td></tr>';
  }

  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    const fd = new FormData(form);
    const payload = { name: fd.get('name'), lastName: fd.get('lastName'), email: fd.get('email'), password: fd.get('password'), role: fd.get('role') };
    const res = await fetch('/api/users', {
      method: 'POST', credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    if (res.status === 201) {
      msg.textContent = 'Utilisateur créé.'; msg.style.color = 'green';
      form.reset(); loadUsers();
    } else if (res.status === 409) {
      msg.textContent = 'Cet email existe déjà.'; msg.style.color = 'red';
    } else if (res.status === 403) {
      msg.textContent = 'Accès refusé — admin uniquement.'; msg.style.color = 'red';
    } else {
      const j = await res.json().catch(() => null);
      msg.textContent = j?.error || 'Erreur lors de la création.'; msg.style.color = 'red';
    }
  });

  loadUsers();
});
