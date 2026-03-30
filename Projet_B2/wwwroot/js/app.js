async function postJson(url, data) {
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(data),
    credentials: 'include'
  });
  return res;
}

async function refreshUser() {
  try {
    const res = await fetch('/api/me', { credentials: 'include' });
    const loginLink = document.getElementById('loginLink');
    const logout = document.getElementById('logout');
    const username = document.getElementById('username');
    if (!loginLink || !logout || !username) return;
    if (res.ok) {
      const data = await res.json();
      loginLink.style.display = 'none';
      logout.style.display = 'inline';
      username.textContent = data.user || '';
      username.style.display = 'inline-block';
    } else {
      loginLink.style.display = 'inline';
      logout.style.display = 'none';
      username.textContent = '';
      username.style.display = 'none';
    }
  } catch (e) {
    // ignore network errors
  }
}

document.addEventListener('DOMContentLoaded', () => {
  const loginForm = document.getElementById('loginForm');
  if (loginForm) {
    loginForm.addEventListener('submit', async (e) => {
      e.preventDefault();
      const fd = new FormData(loginForm);
      const res = await postJson('/api/login', { username: fd.get('username'), password: fd.get('password') });
      if (res.ok) location.href = '/Spaces'; // No-op change for tracking
      else document.getElementById('msg').textContent = 'Login failed';
    });
  }

  const addForm = document.getElementById('addForm');
  if (addForm) {
    addForm.addEventListener('submit', async (e) => {
      e.preventDefault();
      const fd = new FormData(addForm);
      const name = fd.get('name');
      const capacity = parseInt(fd.get('capacity')) || 0;
      const res = await postJson('/api/spaces', { name, capacity });
      if (res.ok) {
        addForm.reset();
        loadList();
      } else {
        alert('Failed to add');
      }
    });
  }

  const logout = document.getElementById('logout');
  if (logout) {
    logout.addEventListener('click', async (e) => {
      e.preventDefault();
      await postJson('/api/logout', {});
      // after logout, reload to hit the login page due to server-side auth requirement
      window.location.href = '/Login';
    });
  }

  if (document.getElementById('list')) loadList();
  // Refresh user on page load and redirect to login if not authenticated and not already on /Login
  refreshUser().then(() => {
    const path = window.location.pathname.toLowerCase();
    if (path !== '/login' && document.getElementById('loginForm') == null) {
      // If user is not authenticated, the server will redirect API calls to 401; attempt a quick check
      fetch('/api/me', { credentials: 'include' }).then(r => {
        if (!r.ok) window.location.href = '/Login';
      }).catch(() => { /* ignore */ });
    }
  });
});

async function loadList() {
  const res = await fetch('/api/spaces', { credentials: 'include' });
  if (res.status === 401) { location.href = '/Login'; return; }
  const items = await res.json();
  const ul = document.getElementById('list');
  ul.innerHTML = '';
  for (const it of items) {
    const li = document.createElement('li');
    li.textContent = `${it.name} (capacity: ${it.capacity}) `;
    const del = document.createElement('button');
    del.textContent = 'Delete';
    del.addEventListener('click', async () => {
      const r = await fetch(`/api/spaces/${it.id}`, { method: 'DELETE', credentials: 'include' });
      if (r.ok) loadList();
    });
    li.appendChild(del);
    ul.appendChild(li);
  }
}
