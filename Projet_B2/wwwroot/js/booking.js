document.addEventListener('DOMContentLoaded', () => {
  (async function(){
    const container = document.getElementById('bookingContainer');
    if (!container) return;

    const spaceSel = document.getElementById('spaceSelect');
    const dateInput = document.getElementById('dateInput');
    const startInput = document.getElementById('startInput');
    const hoursInput = document.getElementById('hoursInput');
    const bookBtn = document.getElementById('bookBtn');
    const msg = document.getElementById('bookingMsg');

    let rooms = [];

    let typeFilter = '';

    function applyTypeFilter(){
      const filtered = typeFilter ? rooms.filter(r => r.type === typeFilter) : rooms;
      spaceSel.innerHTML = '';
      for (const s of filtered){
        const opt = document.createElement('option');
        const priceLabel = (s.pricePerHour != null) ? ` - ${s.pricePerHour.toFixed(2)} EUR/h` : '';
        const typeLabel = s.type ? ` - ${s.type}` : '';
        opt.value = s.id; opt.textContent = `${s.name} (cap. ${s.capacity})${typeLabel}${priceLabel}`;
        spaceSel.appendChild(opt);
      }
      updatePricePreview();
      loadEquipments();
    }

    async function loadEquipments(){
      const eq = document.getElementById('spaceEquipments');
      if (!eq || !spaceSel.value) { if (eq) eq.textContent = ''; return; }
      try {
        const res = await fetch(`/api/spaces/${spaceSel.value}/resources`, { credentials: 'include' });
        if (!res.ok) { eq.textContent = ''; return; }
        const items = await res.json();
        if (!items.length) { eq.innerHTML = '<i class="bi bi-info-circle me-1"></i>Aucun equipement renseigne'; return; }
        eq.innerHTML = items.map(r => `<span class="badge bg-light text-dark border me-1"><i class="bi bi-tools me-1"></i>${r.name}${r.quantity > 1 ? ' x' + r.quantity : ''}</span>`).join('');
      } catch { eq.textContent = ''; }
    }

    document.querySelectorAll('#typeFilter input').forEach(r => r.addEventListener('change', e => {
      typeFilter = e.target.value;
      applyTypeFilter();
    }));

    document.querySelectorAll('[data-slot]').forEach(btn => btn.addEventListener('click', () => {
      const slot = btn.dataset.slot;
      if (slot === 'morning'){ startInput.value = 9; hoursInput.value = 4; }
      if (slot === 'afternoon'){ startInput.value = 14; hoursInput.value = 4; }
      if (slot === 'day'){ startInput.value = 9; hoursInput.value = 9; }
      updatePricePreview();
    }));

    async function loadSpaces(){
      const res = await fetch('/api/spaces', { credentials: 'include' });
      if (!res.ok) return;
      rooms = await res.json();
      applyTypeFilter();
      spaceSel.addEventListener('change', ()=> { fetchBookedSlots(); renderWeek(); loadEquipments(); updatePricePreview(); });
      // if page was opened with query params (from map), pre-select values
      try {
        const params = new URLSearchParams(window.location.search);
        const sid = params.get('spaceId');
        const d = params.get('date');
        const s = params.get('start');
        const h = params.get('hours');
        if (sid) {
          // set if option exists
          const opt = Array.from(spaceSel.options).find(o=>o.value===sid || o.value===String(Number(sid)));
        if (opt) {
          spaceSel.value = opt.value;
          // trigger change handlers only if booking panel is visible
          if (container.style.display !== 'none') { fetchBookedSlots(); renderWeek(); }
        }
        }
        if (d) dateInput.value = d;
        if (s) startInput.value = s;
        if (h) hoursInput.value = h;
      } catch (e) { /* ignore */ }
    }

    // react to a global event from the map if fired
    document.addEventListener('space:selected', (ev)=>{
      try{
        const id = ev?.detail?.id;
        if (id) {
          showBookingForSpace(id);
        }
      }catch(e){ console.warn('[booking] space:selected handler failed', e); }
    });

    // Warm-up FullCalendar to avoid race conditions
    (function warmFullCalendar(){
      function tryInit(){
        if (!window.FullCalendar) return false;
        try {
          if (!document.getElementById('fullCalendarBootstrap')){
            const el = document.createElement('div');
            el.id = 'fullCalendarBootstrap';
            el.style.display = 'none';
            document.body.appendChild(el);
            initFullCalendar('fullCalendarBootstrap');
          }
        } catch (err){ console.warn('[booking] FullCalendar warm-up failed', err); }
        return true;
      }
      if (!tryInit()){
        const t = setInterval(()=>{ if (tryInit()) clearInterval(t); }, 200);
        window.addEventListener('load', ()=>{ tryInit(); });
      }
    })();

    function updatePricePreview(){
      const priceTotal = document.getElementById('priceTotal');
      const priceDetail = document.getElementById('priceDetail');
      if (!priceTotal) return;
      const sp = rooms.find(r => String(r.id) === String(spaceSel.value));
      const hours = Math.max(1, parseInt(hoursInput.value,10) || 1);
      const rate = sp?.pricePerHour ?? 0;
      const ht = rate * hours;
      const ttc = ht * 1.20;
      priceTotal.textContent = `${ttc.toFixed(2)} EUR TTC`;
      priceDetail.textContent = sp ? `(${rate.toFixed(2)} EUR/h x ${hours}h - HT ${ht.toFixed(2)} EUR)` : '';
    }

    function validateSelection(){
      const start = parseInt(startInput.value, 10);
      const hours = parseInt(hoursInput.value, 10);
      if (!spaceSel.value) return 'Choisissez un espace.';
      if (!dateInput.value) return 'Choisissez une date.';
      if (!Number.isInteger(start) || start < 7 || start > 21) return 'Choisissez une heure entre 7h et 21h.';
      if (!Number.isInteger(hours) || hours < 1 || hours > 12) return 'La duree doit etre comprise entre 1h et 12h.';
      if (start + hours > 22) return 'Le creneau doit se terminer au plus tard a 22h.';
      return null;
    }

    spaceSel.addEventListener('change', updatePricePreview);
    hoursInput.addEventListener('input', updatePricePreview);

    bookBtn.addEventListener('click', async (e)=>{
      e.preventDefault();
      const spaceId = parseInt(spaceSel.value, 10);
      const startHour = parseInt(startInput.value, 10);
      const hoursVal = parseInt(hoursInput.value, 10);
      const dateVal = dateInput.value;
      const validationError = validateSelection();
      if (validationError) {
        msg.style.color = 'red';
        msg.textContent = validationError;
        return;
      }
      bookBtn.disabled = true;
      msg.style.color = '';
      msg.textContent = 'Reservation en cours...';
      const attendeesText = (document.getElementById('attendeesInput')?.value || '').trim();
      const attendees = attendeesText ? attendeesText.split(/[,;\s]+/).filter(s => s.includes('@')) : [];
      const payload = {
        spaceId,
        date: dateVal,
        startHour,
        hours: hoursVal,
        attendees
      };
      try {
        const res = await fetch('/api/reservations', { method: 'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(payload), credentials: 'include' });
        if (res.ok){
          const data = await res.json().catch(()=>null);
          const inv = data?.invoiceNumber ? ` - Facture ${data.invoiceNumber}` : '';
          msg.innerHTML = `Reservation confirmee${inv} <a href="/MyReservations" class="ms-2">Voir mes reservations</a>`;
          msg.style.color = 'green';
          await fetchBookedSlots(); renderWeek();
        } else if (res.status === 409) {
          const j = await res.json().catch(()=>null);
          msg.textContent = j?.error || 'Creneau deja reserve';
          msg.style.color = 'red';
        } else {
          const j = await res.json().catch(()=>null);
          msg.textContent = j?.error || 'Reservation impossible'; msg.style.color='red';
        }
      } finally {
        bookBtn.disabled = false;
      }
    });

    async function fetchBookedSlots(){
      const out = document.getElementById('bookedList') || createBookedList();
      out.innerHTML = 'Chargement...';
      const sp = parseInt(spaceSel.value,10);
      if (!sp) { out.innerHTML = 'Aucun espace selectionne'; return; }
      const d = dateInput.value || new Date().toISOString().slice(0,10);
      const res = await fetch(`/api/reservations/space?spaceId=${sp}&date=${encodeURIComponent(d)}`, { credentials: 'include' });
      if (!res.ok) { out.innerHTML = 'Chargement des reservations impossible'; return; }
      const items = await res.json();
      out.innerHTML = '';
      if (!items.length) { out.innerHTML = 'Aucune reservation pour cette date'; return; }
      const ul = document.createElement('ul');
      items.forEach(it=>{
        const li = document.createElement('li');
        const start = it.startHour ?? (it.start ? new Date(it.start).getHours() : 0);
        const hours = it.hours ?? Math.max(1, Math.round((new Date(it.end)-new Date(it.start))/3600000));
        li.textContent = `${it.date || ''} ${start}:00 pendant ${hours}h — ${it.ownerName || it.status || ''}`;
        if (it.status === 'Booked') {
          const btn = document.createElement('button');
          btn.textContent = 'Annuler';
          btn.className = 'btn btn-sm btn-outline-danger ms-2';
          btn.addEventListener('click', async () => {
            if (!confirm(`Annuler la reservation du ${it.date} a ${start}:00 ?`)) return;
            const r = await fetch(`/api/reservations/${it.id}`, { method: 'DELETE', credentials: 'include' });
            if (r.ok || r.status === 204) { await fetchBookedSlots(); await renderWeek(); }
            else { const j = await r.json().catch(()=>null); alert(j?.error || 'Annulation impossible'); }
          });
          li.appendChild(btn);
        }
        ul.appendChild(li);
      });
      out.appendChild(ul);
    }

    function createBookedList(){
      const el = document.createElement('div');
      el.id = 'bookedList';
      const form = document.querySelector('#bookingContainer form') || document.getElementById('bookingForm');
      (form || document.querySelector('#bookingContainer')).appendChild(el);
      return el;
    }

    dateInput.value = new Date().toISOString().slice(0,10);
    startInput.value = '9'; hoursInput.value = '1';

    // week navigation state
    let weekStart = startOfWeek(new Date(dateInput.value));

    document.getElementById('prevWeek').addEventListener('click', ()=> { weekStart = addDays(weekStart, -7); renderWeek(); });
    document.getElementById('nextWeek').addEventListener('click', ()=> { weekStart = addDays(weekStart, 7); renderWeek(); });
    document.getElementById('todayBtn').addEventListener('click', ()=> { weekStart = startOfWeek(new Date()); renderWeek(); });
    dateInput.addEventListener('change', ()=> { weekStart = startOfWeek(new Date(dateInput.value)); renderWeek(); fetchBookedSlots(); });

    await loadSpaces();
    updatePricePreview();
    // do not auto-render bookings on page load when the booking panel is hidden;
    // render only when the panel is visible (e.g. after user clicks a room)
    if (container.style.display !== 'none') {
      await fetchBookedSlots();
      await renderWeek();
    }
    // FullCalendar is intentionally disabled on the map pages to keep the UI compact.
    // (Do not auto-initialize the full calendar here.)

    // utilities
    function startOfWeek(d){ const dt = new Date(d); const day = dt.getDay(); const diff = (day + 6) % 7; dt.setDate(dt.getDate()-diff); dt.setHours(0,0,0,0); return dt; }
    function addDays(d,n){ const r = new Date(d); r.setDate(r.getDate()+n); return r; }

    // render week availability grid
    async function renderWeek(){
      const grid = document.getElementById('weekView');
      const lbl = document.getElementById('weekLabel');
      grid.innerHTML = 'Chargement...';
      const sp = parseInt(spaceSel.value,10);
      if (!sp) { grid.innerHTML = 'Aucun espace selectionne'; lbl.textContent = ''; return; }
      const room = rooms.find(r=>r.id == sp) || { capacity: 1 };
      // build days
      const days = [];
      for (let i=0;i<7;i++){ days.push(addDays(weekStart,i)); }
      lbl.textContent = `${days[0].toISOString().slice(0,10)} a ${days[6].toISOString().slice(0,10)}`;

      // fetch bookings for each day in parallel
      const promises = days.map(d => fetch(`/api/reservations/space?spaceId=${sp}&date=${d.toISOString().slice(0,10)}`, { credentials: 'include' }).then(r=> r.ok? r.json(): []));
      const results = await Promise.all(promises);

      grid.innerHTML = '';
      // mark grid container for styling
      grid.classList.add('availability-grid');
      const table = document.createElement('table'); table.className = 'table table-sm table-responsive';
      const thead = document.createElement('thead');
      const headRow = document.createElement('tr');
      headRow.innerHTML = '<th style="width:60px">Heure</th>' + days.map(d=>`<th>${d.toLocaleDateString(undefined,{weekday:'short',month:'short',day:'numeric'})}</th>`).join('');
      thead.appendChild(headRow); table.appendChild(thead);

      const tbody = document.createElement('tbody');
      // show hours from 7:00 to 21:00 only
      for (let h=7; h<=21; h++){
        const tr = document.createElement('tr');
        const th = document.createElement('th'); th.textContent = `${h}:00`; tr.appendChild(th);
        for (let di=0; di<7; di++){
          const cell = document.createElement('td');
          const dayBookings = results[di] || [];
          // bookings that cover this hour
          const bookingsAtHour = dayBookings.filter(b=>{
            const s = b.startHour ?? (b.start? new Date(b.start).getHours():0);
            const hrs = b.hours ?? Math.max(1, Math.round((b.end && b.start) ? (new Date(b.end)-new Date(b.start))/3600000 : 1));
            return (h >= s) && (h < s+hrs) && (b.status === 'Booked');
          });
          const count = bookingsAtHour.length;
          // semantic classes for styling
          if (count >= room.capacity) {
            cell.className = 'occupied full';
            cell.innerHTML = '<span class="cell-icon">X</span>';
          }
          else if (count > 0) {
            cell.className = 'occupied partial';
            cell.innerHTML = `<span class="cell-count">${count}</span>`;
          }
          else {
            cell.className = 'free';
            cell.innerHTML = '<span class="cell-icon">OK</span>';
          }

          // tooltip and click-to-select
          if (count>0){
            const bk = bookingsAtHour[0];
            const owner = bk.ownerName || ('User#'+(bk.ownerId||'?'));
            const st = bk.status || 'Booked';
              cell.title = `${owner} - ${st}`;
            cell.style.cursor = 'pointer';
            cell.addEventListener('click', ()=>{
              dateInput.value = days[di].toISOString().slice(0,10);
              startInput.value = String(h);
              hoursInput.value = '1';
              document.getElementById('bookingMsg').textContent = `Creneau selectionne : ${dateInput.value} ${h}:00`;
            });
          } else {
            // free cell click selects it too
            cell.style.cursor = 'pointer';
            cell.addEventListener('click', ()=>{
              dateInput.value = days[di].toISOString().slice(0,10);
              startInput.value = String(h);
              hoursInput.value = '1';
              document.getElementById('bookingMsg').textContent = `Creneau selectionne : ${dateInput.value} ${h}:00`;
            });
          }

          tr.appendChild(cell);
        }
        tbody.appendChild(tr);
      }
      table.appendChild(tbody);
      grid.appendChild(table);
      // update legend / capacity indicator
      updateLegend(room.capacity);
    }

    function updateLegend(capacity){
      let legend = document.getElementById('calendarLegend');
      if (!legend){
        legend = document.createElement('div');
        legend.id = 'calendarLegend';
        legend.className = 'mt-2';
        const form = document.querySelector('#bookingContainer') || container;
        (form || container).appendChild(legend);
      }
      legend.innerHTML = `<div class="d-flex gap-2 align-items-center"><span class="legend-item"><span class="badge bg-success me-1">OK</span> Libre</span><span class="legend-item"><span class="badge bg-warning text-dark me-1">#</span> Partiel</span><span class="legend-item"><span class="badge bg-danger me-1">X</span> Complet (capacite ${capacity})</span></div>`;
    }

    function initFullCalendar(target){
      // Accept an optional target element (DOM node or id). If not provided, look for
      // an element with id 'fullCalendar'. Do not auto-run on page load; this function
      // may be called on-demand (for example when a room is clicked on the map).
      const fcEl = (typeof target === 'string') ? document.getElementById(target) : (target || document.getElementById('fullCalendar'));
      if (!fcEl) return;
      // ensure calendar is only initialized once per element
      if (fcEl.dataset.inited) return;
      if (!window.FullCalendar) {
        // FullCalendar library not loaded; leave as no-op
        console.warn('[booking] FullCalendar not available');
        return;
      }
      fcEl.dataset.inited = '1';
      let calendar;
      const options = {
        initialView: 'timeGridWeek',
        nowIndicator: true,
        selectable: true,
        selectMirror: true,
        // add a custom button to open the Booking page for the current space/date
        customButtons: {
          openBooking: {
            text: 'Ouvrir reservation',
            click: function(){
              try {
                const sp = parseInt(spaceSel.value,10) || '';
                const d = (calendar && calendar.getDate) ? calendar.getDate().toISOString().slice(0,10) : (new Date().toISOString().slice(0,10));
                const start = 9;
                window.location.href = `/Booking?spaceId=${encodeURIComponent(sp)}&date=${encodeURIComponent(d)}&start=${encodeURIComponent(start)}`;
              } catch(e){ console.warn('openBooking failed', e); }
            }
          }
        },
        headerToolbar: { left: 'openBooking prev,next today', center: 'title', right: 'timeGridWeek,timeGridDay,dayGridMonth' },
        slotMinTime: '07:00:00', slotMaxTime: '21:00:00',
        height: 'auto',
        dayMaxEventRows: true,
        events: async function(fetchInfo, successCallback, failureCallback){
          try {
            const sp = parseInt(spaceSel.value,10);
            if (!sp) { successCallback([]); return; }
            const start = new Date(fetchInfo.start);
            const end = new Date(fetchInfo.end);
            const events = [];
            // small palette to color events by owner
            const palette = ['#7c3aed','#06b6d4','#f97316','#ef4444','#10b981','#f59e0b','#6366f1'];
            // iterate days in range
            for (let d = new Date(start); d < end; d.setDate(d.getDate()+1)){
              const day = d.toISOString().slice(0,10);
              const res = await fetch(`/api/reservations/space?spaceId=${sp}&date=${day}`, { credentials: 'include' });
              if (!res.ok) continue;
              const items = await res.json();
              items.forEach(it => {
                const s = it.start ? new Date(it.start) : new Date(`${it.date}T${String(it.startHour).padStart(2,'0')}:00:00Z`);
                const e = it.end ? new Date(it.end) : new Date(s.getTime() + ((it.hours||1)*3600000));
                const ownerId = it.ownerId || 0;
                const color = palette[ownerId % palette.length];
                events.push({ id: it.id, title: it.ownerName || (it.status||'Booked'), start: s, end: e, backgroundColor: color, borderColor: color, extendedProps: it });
              });
            }
            successCallback(events);
          } catch (err){ failureCallback(err); }
        },
        select: function(selectInfo){
          // user dragged a time range -> either prefill form or navigate to Booking page
          const start = selectInfo.start; // Date
          const end = selectInfo.end;
          const date = start.toISOString().slice(0,10);
          const hour = start.getUTCHours();
          const hours = Math.max(1, Math.round((end - start) / 3600000));
          // if this calendar is the modal preview, navigate to the Booking page
          if (fcEl && (fcEl.id === 'fullCalendarModal' || fcEl.id === 'fullCalendarMap')){
            const sp = parseInt(spaceSel.value,10) || '';
            window.location.href = `/Booking?spaceId=${encodeURIComponent(sp)}&date=${encodeURIComponent(date)}&start=${encodeURIComponent(hour)}&hours=${encodeURIComponent(hours)}`;
            return;
          }
          // otherwise prefill the compact booking form
          dateInput.value = date;
          startInput.value = String(hour);
          hoursInput.value = String(hours);
          document.getElementById('bookingMsg').textContent = `Creneau selectionne : ${date} ${hour}:00 (${hours}h)`;
          calendar.unselect();
        },
        eventClick: function(info){
          // clicking an event fills the booking inputs for editing/replicating
          const p = info.event.extendedProps || {};
          const s = info.event.start;
          const date = s.toISOString().slice(0,10);
          const hour = s.getUTCHours();
          const hours = Math.max(1, Math.round((info.event.end - s)/3600000));
          // if in modal preview navigate to Booking page for this time
          if (fcEl && (fcEl.id === 'fullCalendarModal' || fcEl.id === 'fullCalendarMap')){
            const sp = parseInt(spaceSel.value,10) || '';
            window.location.href = `/Booking?spaceId=${encodeURIComponent(sp)}&date=${encodeURIComponent(date)}&start=${encodeURIComponent(hour)}&hours=${encodeURIComponent(hours)}`;
            return;
          }
          dateInput.value = date;
          startInput.value = String(hour);
          hoursInput.value = String(hours);
          document.getElementById('bookingMsg').textContent = `Reservation selectionnee : ${info.event.title}`;
        },
        eventDidMount: function(info){
          // attach bootstrap popover with richer details
          const props = info.event.extendedProps || {};
          const owner = props.ownerName || 'Inconnu';
          const status = props.status || 'Booked';
          const total = props.total ?? props.Total_Amount ?? '';
          const t = `<div style="min-width:200px"><strong>${info.event.title}</strong><div class=\"text-muted small\">${status}</div><div style=\"margin-top:6px\">${new Date(info.event.start).toLocaleString()} - ${new Date(info.event.end).toLocaleString()}</div><div class=\"mt-2 small\"><strong>Client:</strong> ${owner}</div><div class=\"small\"><strong>Montant:</strong> ${total}</div></div>`;
          // use popper-based bootstrap popover
          new bootstrap.Popover(info.el, { content: t, html: true, trigger: 'hover', placement: 'auto' });
        }
      };
      calendar = new FullCalendar.Calendar(fcEl, options);
      calendar.render();
      // re-load when space changes
      spaceSel.addEventListener('change', ()=> calendar.refetchEvents());
    }

    // ---------- Cart ----------
    const CART_KEY = 'cw_cart_v1';
    const cartCard = document.getElementById('cartCard');
    const cartList = document.getElementById('cartList');
    const cartBadge = document.getElementById('cartBadge');
    const cartTotal = document.getElementById('cartTotal');

    function readCart(){ try { return JSON.parse(localStorage.getItem(CART_KEY) || '[]'); } catch { return []; } }
    function writeCart(items){ localStorage.setItem(CART_KEY, JSON.stringify(items)); renderCart(); }
    function renderCart(){
      const items = readCart();
      if (!cartCard) return;
      cartCard.style.display = items.length ? '' : 'none';
      cartBadge.textContent = items.length;
      let total = 0;
      cartList.innerHTML = '';
      for (let i = 0; i < items.length; i++){
        const it = items[i];
        const li = document.createElement('li');
        li.className = 'list-group-item d-flex justify-content-between align-items-center px-0';
        li.innerHTML = `
          <div>
            <div class="fw-semibold">${it.spaceName}</div>
              <div class="text-muted small">${it.date} - ${String(it.startHour).padStart(2,'0')}:00 (${it.hours}h)</div>
          </div>
          <div class="d-flex align-items-center gap-2">
                <span class="text-muted small">${(it.totalTtc).toFixed(2)} EUR</span>
            <button class="btn btn-sm btn-link text-danger p-0" data-idx="${i}"><i class="bi bi-x-circle"></i></button>
          </div>`;
        li.querySelector('button').addEventListener('click', () => {
          const arr = readCart(); arr.splice(i, 1); writeCart(arr);
        });
        cartList.appendChild(li);
        total += it.totalTtc;
      }
      cartTotal.textContent = total.toFixed(2) + ' EUR';
    }

    document.getElementById('cartBtn')?.addEventListener('click', () => {
      const sp = rooms.find(r => String(r.id) === String(spaceSel.value));
      const validationError = validateSelection();
      if (validationError) { msg.style.color = 'red'; msg.textContent = validationError; return; }
      if (!sp) { alert('Choisissez un espace.'); return; }
      const hours = Math.max(1, parseInt(hoursInput.value, 10) || 1);
      const item = {
        spaceId: parseInt(spaceSel.value, 10),
        spaceName: sp.name,
        date: dateInput.value,
        startHour: parseInt(startInput.value, 10) || 0,
        hours,
        totalTtc: +(sp.pricePerHour * hours * 1.20).toFixed(2)
      };
      const arr = readCart(); arr.push(item); writeCart(arr);
      msg.style.color = 'green'; msg.textContent = `Ajoute au panier (${arr.length})`;
    });

    document.getElementById('cartClear')?.addEventListener('click', () => writeCart([]));

    document.getElementById('checkoutBtn')?.addEventListener('click', async () => {
      const items = readCart();
      if (!items.length) return;
      const res = await fetch('/api/cart/checkout', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, credentials: 'include',
        body: JSON.stringify({ items: items.map(i => ({ spaceId: i.spaceId, date: i.date, startHour: i.startHour, hours: i.hours })) })
      });
      if (res.ok){
        const data = await res.json();
        writeCart([]);
        msg.style.color = 'green';
        msg.innerHTML = `Panier valide - Facture ${data.invoiceNumber} (${data.totalTtc.toFixed(2)} EUR) <a href="/MyReservations" class="ms-2">Mes reservations</a>`;
        await fetchBookedSlots(); renderWeek();
      } else if (res.status === 409) {
        const j = await res.json().catch(()=>null);
        msg.style.color = 'red'; msg.textContent = j?.error || 'Conflit de reservation';
      } else {
        const j = await res.json().catch(()=>null);
        msg.style.color = 'red'; msg.textContent = j?.error || 'Validation impossible';
      }
    });

    renderCart();

    // expose API for other scripts to show booking UI for a space
    window.initFullCalendar = initFullCalendar;

    window.showBookingForSpace = async function(spaceId){
      // ensure spaces/options are loaded
      await loadSpaces();
      // reveal panel
      // force-visible (use block to avoid CSS specificity issues)
      try {
        // Remove any static inline style attribute (e.g. "display:none") set in the markup
        // so runtime style changes are not masked by the original attribute.
        if (container.hasAttribute && container.hasAttribute('style')) container.removeAttribute('style');
        container.style.setProperty('display','block','important');
        container.style.setProperty('visibility','visible','important');
        container.style.setProperty('opacity','1','important');
        container.hidden = false;
        container.classList.remove('d-none');
      } catch (e) { console.warn('[booking] failed to force-show bookingContainer', e); }

      // help UX: bring booking panel into view and add a brief highlight so it's obvious
      try {
        container.scrollIntoView({ behavior: 'smooth', block: 'center' });
        container.style.setProperty('box-shadow', '0 12px 30px rgba(37,99,235,0.14)', 'important');
        container.style.setProperty('border', '1px solid rgba(37,99,235,0.18)', 'important');
        container.style.setProperty('z-index', '999', 'important');
        setTimeout(()=>{
          container.style.removeProperty('box-shadow');
          container.style.removeProperty('border');
          container.style.removeProperty('z-index');
        }, 3000);
      } catch(e){ /* ignore */ }
      // ensure the select contains the requested space
      let opt = Array.from(spaceSel.options).find(o => o.value == spaceId || o.value === String(Number(spaceId)));
      if (!opt) {
        opt = document.createElement('option');
        opt.value = String(spaceId);
        opt.textContent = `Space ${spaceId}`;
        spaceSel.appendChild(opt);
      }
      spaceSel.value = opt.value;
      // load bookings and render week
      await fetchBookedSlots();
      await renderWeek();
    };

  })();
});
