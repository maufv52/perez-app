let inventory = [];
let orders = [];
let requests = [];
let pendingPhoto = '';
let currentUser = '';
let currentRole = '';
let csrfToken = '';
let csrfRequest = null;
let saleSubmissionId = crypto.randomUUID();
let pendingReceiptOutput = null;

function canManageInventory() {
  return currentRole === 'Admin';
}

const loginScreen = document.getElementById('loginScreen');
const appScreen = document.getElementById('appScreen');
const loginForm = document.getElementById('loginForm');
const saleForm = document.getElementById('saleForm');
const productForm = document.getElementById('productForm');
const saleProduct = document.getElementById('saleProduct');
const paymentPlan = document.getElementById('paymentPlan');
const installments = document.getElementById('installments');
const receiptOutputDialog = document.getElementById('receiptOutputDialog');
const watermarkButton = document.getElementById('watermarkButton');

watermarkButton.addEventListener('click', () => {
  const isOpen = watermarkButton.parentElement.classList.toggle('is-open');
  watermarkButton.setAttribute('aria-expanded', String(isOpen));
});

async function refreshCsrf() {
  if (csrfRequest) return csrfRequest;
  csrfRequest = (async () => {
    const response = await fetch('/api/auth/csrf', { cache: 'no-store' });
    if (!response.ok) throw new Error('No se pudo iniciar la sesión de seguridad. Recarga la página.');
    csrfToken = (await response.json()).token;
  })();
  try { await csrfRequest; } finally { csrfRequest = null; }
}

async function api(path, { method = 'GET', body } = {}) {
  if (method !== 'GET' && !csrfToken) await refreshCsrf();
  const response = await fetch(`/api${path}`, {
    method,
    cache: 'no-store',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  if (!response.ok) {
    let detail = `Error del servidor (${response.status}).`;
    const contentType = response.headers.get('content-type') || '';
    if (contentType.includes('json')) {
      const problem = await response.json();
      detail = problem.detail || problem.title || detail;
    }
    if (response.status === 429) detail = 'Demasiados intentos. Espera cinco minutos antes de volver a intentar.';
    if (response.status === 401 && path !== '/auth/login') {
      showLogin();
      detail = 'Tu sesión venció. Inicia sesión de nuevo.';
      document.getElementById('loginError').textContent = detail;
    }
    const error = new Error(detail);
    error.status = response.status;
    throw error;
  }
  return response.status === 204 ? null : response.json();
}

async function loadState() {
  const state = await api('/state');
  inventory = state.inventory;
  orders = state.orders;
  requests = state.requests;
  renderAll();
}

function showLogin() {
  currentUser = '';
  currentRole = '';
  csrfToken = '';
  inventory = [];
  orders = [];
  requests = [];
  pendingPhoto = '';
  pendingReceiptOutput = null;
  receiptOutputDialog.close();
  renderAll();
  saleForm.reset();
  productForm.reset();
  document.getElementById('requestForm').reset();
  document.getElementById('requestDate').value = new Date().toISOString().slice(0, 10);
  document.getElementById('photoPreview').replaceChildren();
  installments.classList.add('hidden');
  installments.querySelectorAll('input').forEach(input => { input.required = false; });
  saleSubmissionId = crypto.randomUUID();
  appScreen.classList.add('hidden');
  loginScreen.classList.remove('hidden');
}

async function showApp(user) {
  currentUser = user.username;
  currentRole = user.role;
  await refreshCsrf();
  await loadState();
  document.getElementById('welcomeUser').textContent = `Hola, ${currentUser}`;
  loginScreen.classList.add('hidden');
  appScreen.classList.remove('hidden');
  showView('ventasView');
}

async function runAction(element, messageId, action) {
  if (element.dataset.busy === 'true') return;
  element.dataset.busy = 'true';
  if (element.matches('form')) element.inert = true;
  const buttons = [...element.querySelectorAll('button[type="submit"]')];
  if (element.matches('button')) buttons.push(element);
  buttons.forEach(button => { button.disabled = true; });
  document.getElementById(messageId).textContent = '';
  try {
    await action();
  } catch (error) {
    console.error('No se pudo completar la operación:', error);
    document.getElementById(messageId).textContent = error.message || 'No se pudo conectar al servidor.';
  } finally {
    element.dataset.busy = 'false';
    if (element.matches('form')) element.inert = false;
    buttons.forEach(button => { button.disabled = false; });
  }
}

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, character => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  })[character]);
}

function formatMoney(value) {
  return `₡${Number(value || 0).toLocaleString('es-CR')}`;
}

function printReceipt(title, rows) {
  const printWindow = window.open('', '_blank', 'width=380,height=650');
  if (!printWindow) {
    alert('Permite las ventanas emergentes para imprimir el comprobante.');
    return;
  }
  const content = rows.map(([label, value]) => `<p><strong>${escapeHtml(label)}:</strong> ${escapeHtml(value)}</p>`).join('');
  printWindow.document.write(`<!DOCTYPE html><html lang="es"><head><meta charset="UTF-8"><title>${escapeHtml(title)}</title><style>
    @page { size: 58mm auto; margin: 3mm; }
    * { box-sizing: border-box; } body { width: 52mm; margin: 0 auto; color: #000; font: 12px Arial, sans-serif; }
    h1 { margin: 0 0 12px; font-size: 18px; text-align: center; } p { margin: 5px 0; overflow-wrap: anywhere; }
    .rule { border-top: 1px dashed #000; margin: 10px 0; }
    @media print { body { width: 52mm; } }
  </style></head><body><h1>${escapeHtml(title)}</h1><div class="rule"></div>${content}<div class="rule"></div><p style="text-align:center">Gracias</p><script>window.onload = () => { window.print(); window.onafterprint = () => window.close(); };<\/script></body></html>`);
  printWindow.document.close();
}

function invoiceRows(order) {
  const dates = order.installmentDates && order.installmentDates.length
    ? order.installmentDates.map((date, index) => `Cuota ${index + 1}: ${date}`).join(' | ')
    : 'Pago de contado';
  return [
    ['Factura', order.invoice], ['Fecha', new Date(order.createdAt).toLocaleString('es-CR')],
    ['Cliente', `${order.name} ${order.lastName}`], ['Cédula', order.customerId],
    ['Teléfono', order.phone], ['Dirección', order.address], ['Artículo', order.product],
    ['Precio', formatMoney(order.price)], ['Forma de pago', order.plan], ['Método de pago', order.method],
    ['Cuotas', dates], ['Vendedor', order.seller]
  ];
}

function chooseReceiptOutput(title, rows) {
  pendingReceiptOutput = { title, rows };
  receiptOutputDialog.showModal();
}

function completeReceiptOutput() {
  if (!pendingReceiptOutput) return;
  const { title, rows } = pendingReceiptOutput;
  receiptOutputDialog.close();
  printReceipt(title, rows);
}

receiptOutputDialog.addEventListener('close', () => { pendingReceiptOutput = null; });
document.getElementById('receiptPrintButton').addEventListener('click', completeReceiptOutput);
document.getElementById('receiptPdfButton').addEventListener('click', completeReceiptOutput);
document.getElementById('printLatestSaleButton').addEventListener('click', () => {
  const latest = orders[orders.length - 1];
  if (latest) chooseReceiptOutput('Factura', invoiceRows(latest));
});

function requestRows(request) {
  return [
    ['Ticket', request.ticket], ['Nombre', request.name], ['Apellidos', request.lastName],
    ['Fecha del pedido', request.requestDate], ['Fecha de entrega', request.deliveryDate],
    ['Descripción', request.description],
    ['Precio aproximado', request.estimatedPrice === '' ? 'No indicado' : formatMoney(request.estimatedPrice)],
    ['Forma de pago', request.plan]
  ];
}

function refreshInvoice() {
  document.getElementById('invoiceNumber').value = 'Se asigna al guardar';
}

function showView(viewId) {
  document.querySelectorAll('.view').forEach(view => view.classList.toggle('hidden', view.id !== viewId));
  document.querySelectorAll('.nav-item').forEach(button => button.classList.toggle('active', button.dataset.view === viewId));
  if (viewId === 'ventasView') refreshInvoice();
}

function renderProductSelect() {
  const available = inventory.filter(item => Number(item.stock) > 0);
  saleProduct.replaceChildren();
  const placeholder = document.createElement('option');
  placeholder.value = '';
  placeholder.textContent = available.length ? 'Selecciona un artículo' : 'Primero agrega artículos al inventario';
  saleProduct.append(placeholder);
  available.forEach(item => {
    const option = document.createElement('option');
    option.value = item.id;
    option.textContent = `${item.name} · ${item.stock} disponibles`;
    saleProduct.append(option);
  });
  saleProduct.disabled = available.length === 0;
  document.getElementById('productHint').textContent = available.length
    ? 'Selecciona un artículo disponible.'
    : 'Agrega artículos o unidades al inventario para registrar una venta.';
}

function renderInventory() {
  const list = document.getElementById('inventoryList');
  const canEdit = canManageInventory();
  document.querySelector('.inventory-actions').classList.toggle('hidden', !canEdit);
  productForm.classList.toggle('hidden', !canEdit);
  document.querySelector('#inventarioView .page-heading .eyebrow').textContent = canEdit ? 'PRODUCTOS' : 'PRODUCTOS · SOLO LECTURA';
  list.replaceChildren();
  document.getElementById('inventoryCount').textContent = inventory.length;
  if (!inventory.length) {
    list.innerHTML = '<div class="empty-state">Aún no hay artículos. Agrega tu primer producto arriba.</div>';
    return;
  }

  inventory.forEach(item => {
    const card = document.createElement('article');
    card.className = 'card product-card';
    if (typeof item.photo === 'string' && item.photo.startsWith('data:image/')) {
      const image = document.createElement('img');
      image.className = 'product-image';
      image.src = item.photo;
      image.alt = item.name;
      card.append(image);
    } else {
      const placeholder = document.createElement('div');
      placeholder.className = 'product-placeholder';
      placeholder.textContent = '▧';
      card.append(placeholder);
    }

    const info = document.createElement('div');
    info.className = 'product-info';
    const name = document.createElement('h3');
    name.textContent = item.name;
    const details = document.createElement('p');
    details.textContent = item.details || 'Sin detalles adicionales';
    const cost = document.createElement('p');
    cost.textContent = item.cost === undefined || item.cost === null || item.cost === ''
      ? 'Costo: No registrado'
      : `Costo: ${formatMoney(item.cost)}`;
    const stock = document.createElement('span');
    stock.className = 'stock-tag';
    stock.textContent = `${item.stock} unidades`;
    const salePrice = document.createElement('p');
    salePrice.textContent = item.salePrice === undefined || item.salePrice === null
      ? 'Valor de venta: No registrado'
      : `Valor de venta: ${formatMoney(item.salePrice)}`;
    info.append(name, details, cost, salePrice, stock);

    if (canEdit) {
      const remove = document.createElement('button');
      remove.className = 'delete-button';
      remove.type = 'button';
      remove.textContent = 'Eliminar';
      remove.setAttribute('aria-label', `Eliminar ${item.name} del inventario`);
      remove.addEventListener('click', () => runAction(remove, 'appMessage', async () => {
        if (!canManageInventory()) return;
        await api(`/products/${encodeURIComponent(item.id)}`, { method: 'DELETE' });
        await loadState();
      }));
      card.append(info, remove);
    } else {
      card.append(info);
    }
    list.append(card);
  });
}

function renderOrders() {
  const list = document.getElementById('ordersList');
  list.replaceChildren();
  document.getElementById('orderCount').textContent = orders.length;
  if (!orders.length) {
    list.innerHTML = '<div class="empty-state">Todavía no hay ventas. Las facturas guardadas aparecerán aquí.</div>';
    return;
  }

  [...orders].reverse().forEach(order => {
    const card = document.createElement('article');
    card.className = 'card order-card';
    const top = document.createElement('div');
    top.className = 'order-top';
    const invoice = document.createElement('strong');
    invoice.textContent = order.invoice;
    const price = document.createElement('span');
    price.className = 'order-price';
    price.textContent = formatMoney(order.price);
    top.append(invoice, price);

    const info = document.createElement('div');
    info.className = 'order-info';
    const customer = document.createElement('h3');
    customer.textContent = `${order.name} ${order.lastName}`;
    const product = document.createElement('p');
    product.textContent = `${order.product} · ${order.plan} · ${order.method}`;
    const details = document.createElement('p');
    details.className = 'order-details';
    const dates = order.installmentDates && order.installmentDates.length
      ? `Fechas de cuotas: ${order.installmentDates.join(' · ')}`
      : 'Pago de contado';
    details.textContent = `Cédula: ${order.customerId} · Teléfono: ${order.phone} · ${dates}`;
    const address = document.createElement('p');
    address.textContent = `Dirección: ${order.address}`;
    const seller = document.createElement('p');
    seller.textContent = `Registrado por ${order.seller} · ${new Date(order.createdAt).toLocaleDateString('es-CR')}`;
    info.append(customer, product, details, address, seller);
    const actions = document.createElement('div');
    actions.className = 'receipt-actions';
    const viewButton = document.createElement('button');
    viewButton.className = 'button secondary';
    viewButton.type = 'button';
    viewButton.textContent = 'Ver factura';
    viewButton.addEventListener('click', () => showReceipt('Factura', invoiceRows(order)));
    const printButton = document.createElement('button');
    printButton.className = 'button secondary';
    printButton.type = 'button';
    printButton.textContent = 'Imprimir / PDF 58 mm';
    printButton.addEventListener('click', () => chooseReceiptOutput('Factura', invoiceRows(order)));
    actions.append(viewButton, printButton);
    card.append(top, info, actions);
    list.append(card);
  });
}

function showReceipt(title, rows) {
  const details = rows.map(([label, value]) => `<p><strong>${escapeHtml(label)}:</strong> ${escapeHtml(value)}</p>`).join('');
  const receiptWindow = window.open('', '_blank', 'width=420,height=700');
  if (!receiptWindow) {
    alert('Permite las ventanas emergentes para ver la factura.');
    return;
  }
  receiptWindow.document.write(`<!DOCTYPE html><html lang="es"><head><meta charset="UTF-8"><title>${escapeHtml(title)}</title><style>body{max-width:320px;margin:24px auto;padding:18px;font:14px Arial,sans-serif;color:#172a27}h1{text-align:center;color:#176b57}p{padding:7px 0;border-bottom:1px solid #e5ece9;overflow-wrap:anywhere}button{width:100%;padding:12px;margin-top:12px;background:#176b57;color:white;border:0;border-radius:8px;font-size:14px}</style></head><body><h1>${escapeHtml(title)}</h1>${details}<button onclick="window.print()">Imprimir</button></body></html>`);
  receiptWindow.document.close();
}

function renderRequests() {
  const list = document.getElementById('requestsList');
  list.replaceChildren();
  document.getElementById('requestCount').textContent = requests.length;
  if (!requests.length) {
    list.innerHTML = '<div class="empty-state">Todavía no hay pedidos guardados.</div>';
    return;
  }
  [...requests].reverse().forEach(request => {
    const card = document.createElement('article');
    card.className = 'card order-card';
    const top = document.createElement('div');
    top.className = 'order-top';
    const ticket = document.createElement('strong');
    ticket.textContent = request.ticket;
    const price = document.createElement('span');
    price.className = 'order-price';
    price.textContent = request.estimatedPrice === '' ? 'Precio pendiente' : formatMoney(request.estimatedPrice);
    top.append(ticket, price);
    const info = document.createElement('div');
    info.className = 'order-info';
    const customer = document.createElement('h3');
    customer.textContent = `${request.name} ${request.lastName}`;
    const description = document.createElement('p');
    description.textContent = request.description;
    const dates = document.createElement('p');
    dates.className = 'order-details';
    dates.textContent = `Pedido: ${request.requestDate} · Entrega: ${request.deliveryDate} · ${request.plan}`;
    info.append(customer, description, dates);
    const actions = document.createElement('div');
    actions.className = 'receipt-actions';
    const viewButton = document.createElement('button');
    viewButton.className = 'button secondary';
    viewButton.type = 'button';
    viewButton.textContent = 'Ver ticket';
    viewButton.addEventListener('click', () => showReceipt('Pedido', requestRows(request)));
    const printButton = document.createElement('button');
    printButton.className = 'button secondary';
    printButton.type = 'button';
    printButton.textContent = 'Imprimir ticket';
    printButton.addEventListener('click', () => printReceipt('Pedido', requestRows(request)));
    actions.append(viewButton, printButton);
    card.append(top, info, actions);
    list.append(card);
  });
}

function renderAll() {
  renderProductSelect();
  renderInventory();
  renderOrders();
  renderRequests();
  document.getElementById('printLatestSaleButton').classList.toggle('hidden', orders.length === 0);
  refreshInvoice();
}

loginForm.addEventListener('submit', event => {
  event.preventDefault();
  runAction(loginForm, 'loginError', async () => {
    const user = await api('/auth/login', {
      method: 'POST',
      body: {
        username: document.getElementById('username').value.trim(),
        password: document.getElementById('password').value
      }
    });
    document.getElementById('password').value = '';
    await showApp(user);
  });
});

document.getElementById('logoutButton').addEventListener('click', event => {
  runAction(event.currentTarget, 'appMessage', async () => {
    await api('/auth/logout', { method: 'POST' });
    showLogin();
    loginForm.reset();
    await refreshCsrf();
  });
});

document.querySelectorAll('.nav-item').forEach(button => {
  button.addEventListener('click', () => {
    showView(button.dataset.view);
    runAction(button, 'appMessage', loadState);
  });
});
document.getElementById('refreshButton').addEventListener('click', event =>
  runAction(event.currentTarget, 'appMessage', loadState));

saleProduct.addEventListener('change', () => {
  const product = inventory.find(item => item.id === saleProduct.value);
  document.getElementById('salePrice').value = product?.salePrice ?? '';
});

paymentPlan.addEventListener('change', () => {
  const isInstallmentPlan = paymentPlan.value === '3 cuotas';
  installments.classList.toggle('hidden', !isInstallmentPlan);
  installments.querySelectorAll('input[type="date"]').forEach(input => {
    input.required = isInstallmentPlan;
  });
});

saleForm.addEventListener('submit', event => {
  event.preventDefault();
  runAction(saleForm, 'saleMessage', async () => {
  const product = inventory.find(item => item.id === saleProduct.value);
  if (!product || Number(product.stock) < 1) {
    throw new Error('El artículo seleccionado ya no tiene unidades disponibles. Actualiza los datos.');
  }

  const price = Number(document.getElementById('salePrice').value);
  if (!Number.isFinite(price) || price < 0) throw new Error('Indica un precio válido.');
  const installmentDates = paymentPlan.value === '3 cuotas'
    ? ['date1', 'date2', 'date3'].map(id => document.getElementById(id).value)
    : [];

  const input = {
    requestId: saleSubmissionId,
    productId: product.id,
    name: document.getElementById('customerName').value.trim(),
    lastName: document.getElementById('customerLastName').value.trim(),
    customerId: document.getElementById('customerId').value.trim(),
    phone: document.getElementById('customerPhone').value.trim(),
    address: document.getElementById('customerAddress').value.trim(),
    price,
    plan: paymentPlan.value,
    method: document.getElementById('paymentMethod').value,
    installmentDates
  };

  const order = await api('/sales', { method: 'POST', body: input });
  saleSubmissionId = crypto.randomUUID();
  saleForm.reset();
  installments.classList.add('hidden');
  installments.querySelectorAll('input[type="date"]').forEach(input => { input.required = false; });
  document.getElementById('saleMessage').textContent = `Venta ${order.invoice} guardada correctamente.`;
  await runAction(document.getElementById('refreshButton'), 'appMessage', loadState);
  chooseReceiptOutput('Factura', invoiceRows(order));
  });
});
saleForm.addEventListener('input', () => { saleSubmissionId = crypto.randomUUID(); });

function imageAsDataUrl(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const image = new Image();
      image.onload = () => {
        const scale = Math.min(1, 700 / Math.max(image.width, image.height));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(image.width * scale));
        canvas.height = Math.max(1, Math.round(image.height * scale));
        canvas.getContext('2d').drawImage(image, 0, 0, canvas.width, canvas.height);
        resolve(canvas.toDataURL('image/jpeg', 0.78));
      };
      image.onerror = reject;
      image.src = reader.result;
    };
    reader.onerror = reject;
    reader.readAsDataURL(file);
  });
}

document.getElementById('productPhoto').addEventListener('change', async event => {
  const file = event.target.files[0];
  pendingPhoto = '';
  const preview = document.getElementById('photoPreview');
  preview.replaceChildren();
  preview.classList.add('hidden');
  if (!file) return;
  const saveButton = productForm.querySelector('button[type="submit"]');
  saveButton.disabled = true;
  try {
    if (file.size > 10 * 1024 * 1024) throw new Error('Selecciona una imagen de menos de 10 MB.');
    pendingPhoto = await imageAsDataUrl(file);
    const image = document.createElement('img');
    image.src = pendingPhoto;
    image.alt = 'Vista previa del artículo';
    preview.append(image);
    preview.classList.remove('hidden');
  } catch (error) {
    console.error('No se pudo cargar la foto:', error);
    document.getElementById('inventoryMessage').textContent = error.message || 'No se pudo cargar la imagen.';
  } finally {
    saveButton.disabled = false;
  }
});

document.getElementById('requestDate').value = new Date().toISOString().slice(0, 10);
document.getElementById('requestForm').addEventListener('submit', event => {
  event.preventDefault();
  const form = event.currentTarget;
  runAction(form, 'requestMessage', async () => {
  const request = {
    name: document.getElementById('requestName').value.trim(),
    lastName: document.getElementById('requestLastName').value.trim(),
    requestDate: document.getElementById('requestDate').value,
    deliveryDate: document.getElementById('deliveryDate').value,
    description: document.getElementById('requestDescription').value.trim(),
    estimatedPrice: document.getElementById('estimatedPrice').value,
    plan: document.getElementById('requestPlan').value
  };
  const saved = await api('/requests', { method: 'POST', body: request });
  form.reset();
  document.getElementById('requestDate').value = new Date().toISOString().slice(0, 10);
  document.getElementById('requestMessage').textContent = `Ticket ${saved.ticket} guardado correctamente.`;
  await runAction(document.getElementById('refreshButton'), 'appMessage', loadState);
  });
});

productForm.addEventListener('submit', event => {
  event.preventDefault();
  runAction(productForm, 'inventoryMessage', async () => {
  if (!canManageInventory()) return;
  const name = document.getElementById('productName').value.trim();
  const cost = Number(document.getElementById('productCost').value);
  const salePrice = Number(document.getElementById('productSalePrice').value);
  const stock = Number(document.getElementById('productStock').value);
  if (!name || !Number.isFinite(cost) || cost < 0 || !Number.isFinite(salePrice) || salePrice < 0
      || !Number.isInteger(stock) || stock < 0)
    throw new Error('Indica un nombre, costo, valor de venta y cantidad válidos.');
  await api('/products', { method: 'POST', body: {
    name,
    details: document.getElementById('productDetails').value.trim(),
    cost,
    salePrice,
    stock,
    photo: pendingPhoto
  } });
  productForm.reset();
  document.getElementById('productStock').value = '1';
  pendingPhoto = '';
  document.getElementById('photoPreview').replaceChildren();
  document.getElementById('photoPreview').classList.add('hidden');
  document.getElementById('inventoryMessage').textContent = 'Artículo agregado al inventario.';
  await runAction(document.getElementById('refreshButton'), 'appMessage', loadState);
  });
});

document.getElementById('exportButton').addEventListener('click', () => {
  if (!canManageInventory()) return;
  const file = new Blob([JSON.stringify({ products: inventory }, null, 2)], { type: 'application/json' });
  const url = URL.createObjectURL(file);
  const link = document.createElement('a');
  link.href = url;
  link.download = 'inventario-ventas-movil.json';
  link.click();
  URL.revokeObjectURL(url);
});

document.getElementById('importButton').addEventListener('click', () => {
  if (canManageInventory()) document.getElementById('importFile').click();
});
document.getElementById('importFile').addEventListener('change', async event => {
  if (!canManageInventory()) {
    event.target.value = '';
    return;
  }
  const file = event.target.files[0];
  if (!file) return;
  try {
    const data = JSON.parse(await file.text());
    const products = Array.isArray(data) ? data : data.products;
    if (!Array.isArray(products) || products.some(item => !item || typeof item.name !== 'string')) {
      throw new Error('Formato inválido');
    }
    if (!confirm('Esto reemplazará todo el inventario compartido para todos los usuarios. ¿Continuar?')) return;
    const normalized = products.map(item => ({
      id: String(item.id || `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`),
      name: item.name.trim(),
      details: String(item.details || ''),
      cost: item.cost === undefined || item.cost === null || item.cost === '' ? null : Number(item.cost),
      salePrice: item.salePrice === undefined || item.salePrice === null || item.salePrice === '' ? null : Number(item.salePrice),
      stock: Number(item.stock),
      photo: item.photo || ''
    }));
    if (normalized.some(item => !Number.isInteger(item.stock) || item.stock < 0
        || (item.cost !== null && (!Number.isFinite(item.cost) || item.cost < 0))
        || (item.salePrice !== null && (!Number.isFinite(item.salePrice) || item.salePrice < 0)))) {
      throw new Error('El archivo contiene costos, valores de venta o unidades inválidos.');
    }
    const result = await api('/products/import', { method: 'POST', body: normalized });
    document.getElementById('inventoryMessage').textContent = `${result.count} artículo(s) importado(s).`;
    await runAction(document.getElementById('refreshButton'), 'appMessage', loadState);
  } catch (error) {
    console.error('No se pudo importar:', error);
    document.getElementById('inventoryMessage').textContent = `No se pudo importar: ${error.message}`;
  } finally {
    event.target.value = '';
  }
});

renderAll();
async function bootstrap() {
  try {
    await refreshCsrf();
    const response = await fetch('/api/auth/me', { cache: 'no-store' });
    if (response.status === 401) return;
    if (!response.ok) throw new Error('No se pudo recuperar la sesión. Recarga la página.');
    await showApp(await response.json());
  } catch (error) {
    console.error('No se pudo iniciar la aplicación:', error);
    document.getElementById('loginError').textContent = error.message || 'No se pudo conectar al servidor.';
  }
}
bootstrap();