using System.Globalization;
using System.Text;
using VisorDatosSIG.Application.Migracion;
using VisorDatosSIG.Application.Seguridad;
using VisorDatosSIG.Domain;
using VisorDatosSIG.Infrastructure.Datos;
using VisorDatosSIG.Infrastructure.Geo;
using VisorDatosSIG.Infrastructure.Seguridad;

namespace VisorDatosSIG.Migrador;

/// <summary>
/// Migrador SHP → SQL Server (RF-MIG-01..14): conexión, operador autenticado, detección y
/// validación de los cuatro SHP, vista previa y mapeo, carga transaccional con progreso y
/// cancelación, resumen exportable y reconstrucción de índices espaciales.
/// </summary>
public sealed class FormularioPrincipal : Form
{
    private static readonly CultureInfo Cultura = CultureInfo.GetCultureInfo("es-BO");
    private readonly ConfiguracionLocal _config = ConfiguracionLocal.Cargar();
    private readonly LectorShapefileNts _lector = new();

    private FabricaConexion? _fabrica;
    private Usuario? _operador;
    private int _versionConexion;
    private readonly List<CapaDetectada> _capas = [];
    private CancellationTokenSource? _cancelacion;
    private ResumenMigracion? _ultimoResumen;

    // Conexión y operador
    private readonly TextBox _txtServidor = new() { Width = 260 };
    private readonly TextBox _txtBase = new() { Width = 260 };
    private readonly ComboBox _cmbAuth = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly TextBox _txtUsuarioSql = new() { Width = 260 };
    private readonly TextBox _txtClaveSql = new() { Width = 260, UseSystemPasswordChar = true };
    private readonly CheckBox _chkCertificado = new() { Text = "Confiar en el certificado del servidor (desarrollo)", AutoSize = true };
    private readonly Label _lblConexion = new() { AutoSize = true, MaximumSize = new Size(620, 0) };
    private readonly TextBox _txtOperador = new() { Width = 260 };
    private readonly TextBox _txtClaveOperador = new() { Width = 260, UseSystemPasswordChar = true };
    private readonly Label _lblOperador = new() { AutoSize = true, MaximumSize = new Size(620, 0) };

    // Archivos y validación
    private readonly TextBox _txtCarpeta = new() { Width = 520, ReadOnly = true };
    private readonly DataGridView _gridCapas = Grilla();
    private readonly DataGridView _gridIncidencias = Grilla();
    private readonly CheckBox _chkSinPrj = new() { Text = "Confirmo que las capas sin archivo .prj están en WGS 84 (se registrará en la bitácora)", AutoSize = true };
    private readonly Button _btnValidar = Boton("Validar");

    // Vista previa
    private readonly ComboBox _cmbVista = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly DataGridView _gridMapeo = Grilla();
    private readonly DataGridView _gridVista = Grilla();

    // Migración
    private readonly RadioButton _rbReemplazar = new() { Text = "Reemplazar de forma segura (borra y recarga las capas elegidas)", AutoSize = true, Checked = true };
    private readonly RadioButton _rbAnexar = new() { Text = "Anexar sin duplicar (omite registros con la misma clave natural)", AutoSize = true };
    private readonly CheckedListBox _lstCapas = new() { Width = 420, Height = 90, CheckOnClick = true };
    private readonly CheckBox _chkDetener = new() { Text = "Detener y revertir si algún registro no se puede convertir", AutoSize = true };
    private readonly Button _btnMigrar = Boton("Migrar", habilitado: false);
    private readonly Button _btnCancelar = Boton("Cancelar", habilitado: false);
    private readonly Button _btnExportar = Boton("Exportar resumen…", habilitado: false);
    private readonly Button _btnIndices = Boton("Reconstruir índices espaciales…", habilitado: false);
    private readonly ProgressBar _barra = new() { Width = 620, Height = 22 };
    private readonly Label _lblProgreso = new() { AutoSize = true, Text = "Sin actividad." };
    private readonly TextBox _txtRegistro = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };

    private readonly TabControl _pestanas = new() { Dock = DockStyle.Fill };

    public FormularioPrincipal()
    {
        Text = "VisorDatosSIG · Migrador SHP → SQL Server";
        MinimumSize = new Size(980, 680);
        Size = new Size(1180, 780);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        var cabecera = new Label
        {
            Text = "  Migrador de datos geográficos · Manzanas, Lotes, Códigos fijos y Vías (WGS 84 · SRID 4326)",
            Dock = DockStyle.Top, Height = 40, TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(31, 78, 121), ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 11f),
        };
        _pestanas.TabPages.Add(PestanaConexion());
        _pestanas.TabPages.Add(PestanaArchivos());
        _pestanas.TabPages.Add(PestanaVistaPrevia());
        _pestanas.TabPages.Add(PestanaMigracion());
        Controls.Add(_pestanas);
        Controls.Add(cabecera);
        _pestanas.Selecting += (_, e) =>
        {
            if (e.TabPageIndex > 0 && _operador is null)
            {
                e.Cancel = true;
                MessageBox.Show("Primero pruebe la conexión e ingrese con un usuario Administrador o Catastro.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        FormClosing += (_, e) =>
        {
            if (_cancelacion is not null)
            {
                e.Cancel = true;
                MessageBox.Show("Hay una migración en curso. Cancélela antes de cerrar.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
    }

    // ================================================================= Pestaña 1: conexión

    private TabPage PestanaConexion()
    {
        _txtServidor.Text = _config.Servidor;
        _txtBase.Text = _config.BaseDatos;
        _cmbAuth.Items.AddRange(["Autenticación de Windows", "Autenticación de SQL Server"]);
        _cmbAuth.SelectedIndex = _config.AutenticacionWindows ? 0 : 1;
        _txtUsuarioSql.Text = _config.UsuarioSql;
        _chkCertificado.Checked = _config.ConfiarCertificado;
        _txtOperador.Text = _config.UltimoOperador;
        void ActualizarAuth() => _txtUsuarioSql.Enabled = _txtClaveSql.Enabled = _cmbAuth.SelectedIndex == 1;
        _cmbAuth.SelectedIndexChanged += (_, _) => ActualizarAuth();
        ActualizarAuth();
        void ConexionModificada(object? sender, EventArgs e)
        {
            _versionConexion++;
            _fabrica = null;
            InvalidarOperador("La conexión cambió. Pruebe la conexión e ingrese nuevamente.");
        }
        _txtServidor.TextChanged += ConexionModificada;
        _txtBase.TextChanged += ConexionModificada;
        _txtUsuarioSql.TextChanged += ConexionModificada;
        _txtClaveSql.TextChanged += ConexionModificada;
        _cmbAuth.SelectedIndexChanged += ConexionModificada;
        _chkCertificado.CheckedChanged += ConexionModificada;

        var btnProbar = Boton("Probar conexión");
        btnProbar.Click += async (_, _) => await ProbarConexionAsync();
        var btnIngresar = Boton("Ingresar");
        btnIngresar.Click += async (_, _) => await IngresarAsync();
        _txtClaveOperador.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await IngresarAsync(); } };

        var t = Tabla(2);
        Titulo(t, "1. Conexión a SQL Server (RF-MIG-01)");
        Fila(t, "Servidor:", _txtServidor);
        Fila(t, "Base de datos:", _txtBase);
        Fila(t, "Autenticación:", _cmbAuth);
        Fila(t, "Usuario SQL:", _txtUsuarioSql);
        Fila(t, "Contraseña SQL:", _txtClaveSql);
        Fila(t, "", _chkCertificado);
        Fila(t, "", btnProbar);
        Fila(t, "", _lblConexion);
        Titulo(t, "2. Operador de la aplicación (perfil Administrador o Catastro)");
        Fila(t, "Usuario:", _txtOperador);
        Fila(t, "Contraseña:", _txtClaveOperador);
        Fila(t, "", btnIngresar);
        Fila(t, "", _lblOperador);
        return new TabPage("1 · Conexión y operador") { Controls = { t }, Padding = new Padding(12) };
    }

    private async Task<bool> ProbarConexionAsync()
    {
        int versionConexion = ++_versionConexion;
        _fabrica = null;
        InvalidarOperador("Ingrese nuevamente después de probar la conexión.");
        _config.Servidor = _txtServidor.Text;
        _config.BaseDatos = _txtBase.Text;
        _config.AutenticacionWindows = _cmbAuth.SelectedIndex == 0;
        _config.UsuarioSql = _txtUsuarioSql.Text;
        _config.ConfiarCertificado = _chkCertificado.Checked;
        _lblConexion.ForeColor = Color.DimGray;
        _lblConexion.Text = "Probando conexión…";
        UseWaitCursor = true;
        try
        {
            var fabrica = new FabricaConexion(_config.CadenaConexion(_txtClaveSql.Text));
            var error = await new MigracionRepositorio(fabrica).ProbarConexionAsync(CancellationToken.None);
            if (versionConexion != _versionConexion) return false;
            if (error is not null)
            {
                _lblConexion.ForeColor = Color.Firebrick;
                _lblConexion.Text = "✗ " + error;
                _fabrica = null;
                return false;
            }
            _fabrica = fabrica;
            _config.Guardar();
            _lblConexion.ForeColor = Color.ForestGreen;
            _lblConexion.Text = $"✓ Conexión correcta a {_config.BaseDatos} en {_config.Servidor}. Ahora ingrese con su usuario.";
            return true;
        }
        catch (ArgumentException ex)
        {
            _lblConexion.ForeColor = Color.Firebrick;
            _lblConexion.Text = "✗ Datos de conexión inválidos: " + ex.Message;
            return false;
        }
        finally { UseWaitCursor = false; }
    }

    private async Task IngresarAsync()
    {
        InvalidarOperador("Validando operador…");
        if (_fabrica is null && !await ProbarConexionAsync()) return;
        var fabrica = _fabrica!;
        int versionConexion = _versionConexion;
        var auth = new AutenticacionServicio(new UsuarioRepositorio(fabrica), new BitacoraRepositorio(fabrica), new HasherPbkdf2());
        UseWaitCursor = true;
        try
        {
            var r = await auth.IniciarSesionAsync(_txtOperador.Text, _txtClaveOperador.Text, $"migrador@{Environment.MachineName}", CancellationToken.None);
            _txtClaveOperador.Clear();
            if (versionConexion != _versionConexion || !ReferenceEquals(fabrica, _fabrica)) return;
            if (!r.Exito) { InvalidarOperador(r.Mensaje ?? "Acceso no válido."); return; }
            if (!r.Usuario!.Roles.Any(x => x is RolesSistema.Administrador or "Catastro"))
            {
                InvalidarOperador("Su perfil no está autorizado para migrar datos (se requiere Administrador o Catastro).");
                return;
            }
            _operador = r.Usuario;
            _config.UltimoOperador = _operador.Login;
            _config.Guardar();
            CambiarEstadoControles(enEjecucion: false);
            MostrarOperador($"✓ Operador: {_operador.Nombre} ({_operador.Login}) · {string.Join(", ", _operador.Roles)}", true);
            _pestanas.SelectedIndex = 1;
        }
        catch (Exception ex)
        {
            InvalidarOperador("No se pudo validar al operador: " + ex.Message);
        }
        finally { UseWaitCursor = false; }
    }

    private void InvalidarOperador(string mensaje)
    {
        _operador = null;
        MostrarOperador(mensaje, false);
        _btnMigrar.Enabled = false;
        _btnIndices.Enabled = false;
    }

    private async Task<bool> ValidarOperadorActualAsync(bool soloAdministrador)
    {
        if (_fabrica is null || _operador is null)
        {
            MessageBox.Show("Pruebe la conexión e ingrese nuevamente antes de continuar.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        var fabrica = _fabrica;
        int versionConexion = _versionConexion;
        int idOperador = _operador.IdUsuario;
        try
        {
            var vigente = await new UsuarioRepositorio(fabrica).ObtenerPorIdAsync(idOperador, CancellationToken.None);
            if (versionConexion != _versionConexion || !ReferenceEquals(fabrica, _fabrica) || _operador?.IdUsuario != idOperador)
                return false;
            if (vigente is null || !vigente.Activo ||
                !(soloAdministrador ? vigente.EsAdministrador : vigente.Roles.Any(r => r is RolesSistema.Administrador or "Catastro")))
            {
                var mensaje = soloAdministrador
                    ? "El operador ya no está activo o no tiene el perfil Administrador requerido para reconstruir índices."
                    : "El operador ya no está activo o no tiene un perfil Administrador o Catastro para migrar datos.";
                InvalidarOperador(mensaje);
                MessageBox.Show(mensaje, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            _operador = vigente;
            MostrarOperador($"✓ Operador: {vigente.Nombre} ({vigente.Login}) · {string.Join(", ", vigente.Roles)}", true);
            return true;
        }
        catch (Exception ex)
        {
            InvalidarOperador("No se pudo comprobar el operador. Ingrese nuevamente.");
            MessageBox.Show("No se pudo comprobar el operador: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void MostrarOperador(string texto, bool ok)
    {
        _lblOperador.ForeColor = ok ? Color.ForestGreen : Color.Firebrick;
        _lblOperador.Text = (ok ? "" : "✗ ") + texto;
    }

    // ================================================================= Pestaña 2: archivos y validación

    private TabPage PestanaArchivos()
    {
        var btnCarpeta = Boton("Seleccionar carpeta…");
        btnCarpeta.Click += (_, _) => SeleccionarCarpeta();
        var btnArchivos = Boton("Seleccionar archivos .shp…");
        btnArchivos.Click += (_, _) => SeleccionarArchivos();
        _btnValidar.Click += async (_, _) => await ValidarAsync();
        _txtCarpeta.Text = _config.UltimaCarpeta;

        _gridCapas.Columns.AddRange(
            Col("Capa", "Capa", 110), Col("Archivo", "Archivo", 200), Col("Componentes", "SHP · SHX · DBF · PRJ · CPG", 190),
            Col("Geometria", "Geometría", 95), Col("Registros", "Registros", 80), Col("Campos", "Campos", 220),
            Col("Extension", "Extensión (lon/lat)", 230), Col("Referencia", "Referencia", 110), Col("Estado", "Validación", 330));
        _gridIncidencias.Columns.AddRange(
            Col("Capa", "Capa", 110), Col("Nivel", "Nivel", 90), Col("Codigo", "Código", 190), Col("Fila", "Fila (FID)", 75),
            Col("Campo", "Campo", 90), Col("Valor", "Valor", 140), Col("Mensaje", "Mensaje", 520));

        var barra = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 6) };
        barra.Controls.AddRange([btnCarpeta, btnArchivos, _txtCarpeta, _btnValidar]);
        var division = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 230 };
        division.Panel1.Controls.Add(_gridCapas);
        var lblInc = new Label { Text = "Incidencias de validación (resumen por código y detalle de las primeras filas):", Dock = DockStyle.Top, Height = 22 };
        division.Panel2.Controls.Add(_gridIncidencias);
        division.Panel2.Controls.Add(lblInc);

        var pagina = new TabPage("2 · Archivos y validación") { Padding = new Padding(12) };
        pagina.Controls.Add(division);
        pagina.Controls.Add(_chkSinPrj);
        pagina.Controls.Add(barra);
        _chkSinPrj.Dock = DockStyle.Top;
        return pagina;
    }

    private void SeleccionarCarpeta()
    {
        using var dialogo = new FolderBrowserDialog { Description = "Carpeta con los Shapefile de Manzanas, Lotes, Códigos fijos y Vías", UseDescriptionForTitle = true };
        if (Directory.Exists(_txtCarpeta.Text)) dialogo.InitialDirectory = _txtCarpeta.Text;
        if (dialogo.ShowDialog(this) != DialogResult.OK) return;
        _txtCarpeta.Text = dialogo.SelectedPath;
        _config.UltimaCarpeta = dialogo.SelectedPath;
        _config.Guardar();
        CargarConjuntos(DefinicionesMigracion.DetectarEnCarpeta(dialogo.SelectedPath));
    }

    private void SeleccionarArchivos()
    {
        using var dialogo = new OpenFileDialog { Filter = "Shapefile (*.shp)|*.shp", Multiselect = true, Title = "Seleccione los archivos .shp" };
        if (Directory.Exists(_txtCarpeta.Text)) dialogo.InitialDirectory = _txtCarpeta.Text;
        if (dialogo.ShowDialog(this) != DialogResult.OK) return;
        _txtCarpeta.Text = Path.GetDirectoryName(dialogo.FileNames[0]) ?? "";
        CargarConjuntos(dialogo.FileNames.Select(f => (DefinicionesMigracion.ReconocerPorNombre(f), ArchivosShapefile.Desde(f))).ToList());
    }

    /// <summary>Detecta los archivos asociados y muestra geometría, registros, campos y extensión (RF-MIG-02/03).</summary>
    private void CargarConjuntos(IReadOnlyList<(DefinicionCapaMigracion? Definicion, ArchivosShapefile Archivos)> conjuntos)
    {
        _capas.Clear();
        _gridCapas.Rows.Clear();
        _gridIncidencias.Rows.Clear();
        foreach (var (def, archivos) in conjuntos)
        {
            var capa = new CapaDetectada(def, archivos);
            if (def is not null && archivos.Shp is not null)
            {
                try { capa.Info = _lector.Inspeccionar(archivos); }
                catch (Exception ex) { capa.EstadoTexto = "No se pudo leer: " + ex.Message; }
            }
            _capas.Add(capa);
            string Marca(string? ruta) => ruta is null ? "✗" : "✓";
            var e = capa.Info?.Extension;
            _gridCapas.Rows.Add(
                def?.Nombre ?? "(no reconocida)", archivos.Nombre,
                $"{Marca(archivos.Shp)} {Marca(archivos.Shx)} {Marca(archivos.Dbf)} {Marca(archivos.Prj)} {Marca(archivos.Cpg)}",
                capa.Info?.TipoGeometria ?? "—", capa.Info?.Registros.ToString("N0", Cultura) ?? "—",
                capa.Info is null ? "—" : string.Join(", ", capa.Info.Campos.Select(c => c.Nombre)),
                e is null ? "—" : string.Create(CultureInfo.InvariantCulture, $"{e.MinX:0.0000}, {e.MinY:0.0000} → {e.MaxX:0.0000}, {e.MaxY:0.0000}"),
                capa.Info?.ContenidoPrj is { } prj ? (ValidadorShapefile.EsWgs84(prj) ? "WGS 84 (4326)" : "NO es WGS 84") : "sin .prj",
                def is null ? "Se ignora: el nombre no corresponde a ninguna capa" : capa.EstadoTexto ?? "Pendiente de validar");
        }
        _lstCapas.Items.Clear();
        _cmbVista.Items.Clear();
        foreach (var c in _capas.Where(c => c.Definicion is not null))
        {
            _lstCapas.Items.Add(c, true);
            _cmbVista.Items.Add(c);
        }
        if (_cmbVista.Items.Count > 0) _cmbVista.SelectedIndex = 0;
        if (_capas.Count(c => c.Definicion is not null) < 4)
            Registrar($"Atención: se reconocieron {_capas.Count(c => c.Definicion is not null)} de las 4 capas esperadas.");
    }

    private async Task ValidarAsync()
    {
        if (_capas.Count == 0) { MessageBox.Show("Seleccione primero la carpeta o los archivos.", Text); return; }
        _btnValidar.Enabled = false;
        _gridIncidencias.Rows.Clear();
        var validador = new ValidadorShapefile(_lector);
        try
        {
            for (int i = 0; i < _capas.Count; i++)
            {
                var c = _capas[i];
                if (c.Definicion is null) continue;
                _gridCapas.Rows[i].Cells["Estado"].Value = "Validando…";
                var progreso = new Progress<int>(n => _gridCapas.Rows[i].Cells["Estado"].Value = $"Validando… {n:N0} registros");
                var r = await Task.Run(() => validador.Validar(c.Definicion, c.Archivos, _chkSinPrj.Checked, progreso));
                c.Validacion = r;
                var celda = _gridCapas.Rows[i].Cells["Estado"];
                celda.Value = r.Bloqueada
                    ? "BLOQUEADA: " + string.Join(" ", r.Incidencias.Detalle.Where(x => x.Codigo.StartsWith("BLOQ_")).Select(x => x.Mensaje))
                    : $"Lista · válidos {r.Validos:N0} · fallidos {r.Fallidos:N0} · duplicados {r.Duplicados:N0} · reparados {r.Reparados:N0}";
                celda.Style.ForeColor = r.Bloqueada ? Color.Firebrick : r.Fallidos > 0 ? Color.DarkOrange : Color.ForestGreen;
                foreach (var inc in r.Incidencias.Resumen().Concat(r.Incidencias.Detalle))
                    _gridIncidencias.Rows.Add(c.Definicion.Nombre, inc.Nivel, inc.Codigo, inc.Fila?.ToString() ?? "", inc.Campo ?? "", inc.Valor ?? "", inc.Mensaje);
            }
        }
        finally { _btnValidar.Enabled = true; }
    }

    // ================================================================= Pestaña 3: vista previa y mapeo

    private TabPage PestanaVistaPrevia()
    {
        _gridMapeo.Columns.AddRange(Col("Origen", "Campo origen (DBF / geometría)", 260), Col("Destino", "Columna destino", 170),
            Col("Tipo", "Tipo SQL", 130), Col("Regla", "Regla / observación", 520));
        _cmbVista.SelectedIndexChanged += (_, _) => MostrarVistaPrevia();
        var arriba = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        arriba.Controls.Add(new Label { Text = "Capa:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        arriba.Controls.Add(_cmbVista);
        var division = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 200 };
        division.Panel1.Controls.Add(_gridMapeo);
        division.Panel1.Controls.Add(new Label { Text = "Mapeo explícito campo origen → columna destino (RF-MIG-05):", Dock = DockStyle.Top, Height = 22 });
        division.Panel2.Controls.Add(_gridVista);
        division.Panel2.Controls.Add(new Label { Text = "Primeros 20 registros (valor origen | valor que se insertará):", Dock = DockStyle.Top, Height = 22 });
        var pagina = new TabPage("3 · Vista previa y mapeo") { Padding = new Padding(12) };
        pagina.Controls.Add(division);
        pagina.Controls.Add(arriba);
        return pagina;
    }

    private void MostrarVistaPrevia()
    {
        if (_cmbVista.SelectedItem is not CapaDetectada { Definicion: { } def } capa) return;
        _gridMapeo.Rows.Clear();
        foreach (var m in def.Mapeo)
            _gridMapeo.Rows.Add(m.DescripcionOrigen, m.ColumnaDestino, m.DescripcionTipo, m.Regla);
        _gridMapeo.Rows.Add("Geometría SHP", "Geom", "geometry (SRID 4326)", "2D (se descarta Z), validada con IsValid/STIsValid; reparación registrada");
        _gridMapeo.Rows.Add("Clave natural", def.DescripcionClave, "", "Evita duplicados en el archivo y en modo Anexar (RF-MIG-11)");

        _gridVista.Columns.Clear();
        _gridVista.Rows.Clear();
        if (capa.Archivos.Shp is null) return;
        try
        {
            var filas = new ValidadorShapefile(_lector).VistaPrevia(def, capa.Archivos, 20);
            if (filas.Count == 0) return;
            _gridVista.Columns.Add("FID", "FID");
            foreach (var k in filas[0].Origen.Keys) _gridVista.Columns.Add("o_" + k, k);
            foreach (var m in def.Mapeo) _gridVista.Columns.Add("d_" + m.ColumnaDestino, "→ " + m.ColumnaDestino);
            _gridVista.Columns.Add("Geom", "Geometría");
            foreach (var f in filas)
            {
                var valores = new List<object?> { f.Fid };
                valores.AddRange(f.Origen.Values.Select(v => (object?)Convert.ToString(v, CultureInfo.InvariantCulture)));
                valores.AddRange(def.Mapeo.Select(m => (object?)(f.Destino is null ? "(error)" : Convert.ToString(f.Destino.GetValueOrDefault(m.ColumnaDestino), CultureInfo.InvariantCulture))));
                valores.Add(f.Geometria);
                _gridVista.Rows.Add(valores.Select(v => v ?? "").ToArray());
            }
            foreach (DataGridViewColumn col in _gridVista.Columns)
                if (col.Name.StartsWith("d_")) col.DefaultCellStyle.BackColor = Color.FromArgb(232, 242, 252);
        }
        catch (Exception ex) { Registrar("Vista previa no disponible: " + ex.Message); }
    }

    // ================================================================= Pestaña 4: migración

    private TabPage PestanaMigracion()
    {
        _btnMigrar.Click += async (_, _) => await MigrarAsync();
        _btnCancelar.Click += (_, _) =>
        {
            _cancelacion?.Cancel();
            _btnCancelar.Enabled = false;
            Registrar("Cancelación solicitada: se revertirá la transacción…");
        };
        _btnExportar.Click += (_, _) => ExportarResumen();
        _btnIndices.Click += async (_, _) => await ReconstruirIndicesAsync();
        _lstCapas.Format += (_, e) => e.Value = e.ListItem is CapaDetectada c ? $"{c.Definicion!.Nombre} ← {c.Archivos.Nombre}" : e.Value;
        _cmbVista.Format += (_, e) => e.Value = e.ListItem is CapaDetectada c ? $"{c.Definicion!.Nombre} ← {c.Archivos.Nombre}" : e.Value;

        var opciones = Tabla(2);
        opciones.Dock = DockStyle.Top;
        Fila(opciones, "Modo de carga:", Apilar(_rbReemplazar, _rbAnexar));
        Fila(opciones, "Capas a migrar:", _lstCapas);
        Fila(opciones, "", _chkDetener);
        Fila(opciones, "", Apilar(horizontal: true, _btnMigrar, _btnCancelar, _btnExportar, _btnIndices));
        Fila(opciones, "Progreso:", _barra);
        Fila(opciones, "", _lblProgreso);

        var pagina = new TabPage("4 · Migración y bitácora") { Padding = new Padding(12) };
        var marco = new GroupBox { Text = "Registro de la ejecución", Dock = DockStyle.Fill, Padding = new Padding(8) };
        marco.Controls.Add(_txtRegistro);
        pagina.Controls.Add(marco);
        pagina.Controls.Add(opciones);
        return pagina;
    }

    private async Task MigrarAsync()
    {
        if (_fabrica is null || _operador is null) return;
        var seleccion = _lstCapas.CheckedItems.Cast<CapaDetectada>().ToList();
        if (seleccion.Count == 0) { MessageBox.Show("Marque al menos una capa.", Text); return; }
        var modo = _rbReemplazar.Checked ? ModoCarga.Reemplazar : ModoCarga.Anexar;
        if (modo == ModoCarga.Reemplazar && MessageBox.Show(
                $"Se REEMPLAZARÁN los datos de: {string.Join(", ", seleccion.Select(c => c.Definicion!.Nombre))}.\n\n" +
                "La operación se ejecuta en una transacción: si algo falla o la cancela, la base queda como estaba.\n¿Desea continuar?",
                "Confirmar reemplazo", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        if (!await ValidarOperadorActualAsync(soloAdministrador: false)) return;

        _txtRegistro.Clear();
        _cancelacion = new CancellationTokenSource();
        CambiarEstadoControles(enEjecucion: true);
        Registrar($"Inicio · operador {_operador.Login} · modo {modo} · {seleccion.Count} capa(s)");
        var progreso = new Progress<ProgresoMigracion>(p =>
        {
            _barra.Maximum = Math.Max(1, p.Total);
            _barra.Value = Math.Min(_barra.Maximum, p.Procesados);
            _lblProgreso.Text = p.Total > 0
                ? $"{p.Capa} · {p.Fase}: {p.Procesados:N0}/{p.Total:N0} · exitosos {p.Exitosos:N0} · omitidos {p.Omitidos:N0} · fallidos {p.Fallidos:N0}"
                : $"{p.Capa} · {p.Fase}";
            Registrar(_lblProgreso.Text, soloSiCambia: true);
        });
        try
        {
            var servicio = new MigracionServicio(_lector, new MigracionRepositorio(_fabrica));
            var solicitud = new SolicitudMigracion(_operador.Login, Environment.MachineName, modo,
                seleccion.Select(c => new CapaAMigrar(c.Definicion!, c.Archivos, _chkSinPrj.Checked)).ToList(),
                DetenerSiHayFilasConError: _chkDetener.Checked);
            var resumen = await Task.Run(() => servicio.EjecutarAsync(solicitud, progreso, _cancelacion.Token));
            _ultimoResumen = resumen;
            MostrarResumen(resumen);
        }
        catch (Exception ex)
        {
            Registrar("ERROR: " + ex.Message);
            MessageBox.Show(ex.Message, "Error en la migración", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cancelacion.Dispose();
            _cancelacion = null;
            CambiarEstadoControles(enEjecucion: false);
        }
    }

    private void MostrarResumen(ResumenMigracion r)
    {
        Registrar("");
        Registrar($"RESULTADO: {r.Estado} en {r.Duracion.TotalSeconds:0.0} s — {r.Mensaje}");
        foreach (var c in r.Capas)
            Registrar($"  {c.Definicion.Nombre,-14} leídos {c.Conteos.Leidos,7:N0} · insertados {c.Conteos.Insertados,7:N0} · omitidos {c.Conteos.Omitidos,5:N0} · fallidos {c.Conteos.Fallidos,4:N0} · reparados {c.Conteos.Reparados} · advertencias {c.Conteos.Advertencias:N0} · bitácora #{c.IdMigracion}");
        if (r.Vinculos is { } v)
            Registrar($"  Vínculos espaciales: códigos con lote {v.CodigosConLote:N0}/{v.TotalCodigos:N0} · lotes con manzana {v.LotesConManzana:N0}/{v.TotalLotes:N0}");
        if (r.ConteosFinales is { } f)
            Registrar("  Conteo final en SQL Server: " + string.Join(" · ", f.Select(kv => $"{kv.Key} {kv.Value:N0}")));
        _barra.Value = r.Exitosa ? _barra.Maximum : 0;
        _lblProgreso.Text = $"{r.Estado}: {r.Mensaje}";
        _lblProgreso.ForeColor = r.Exitosa ? Color.ForestGreen : Color.Firebrick;
        MessageBox.Show(r.Mensaje, $"Migración {r.Estado}", MessageBoxButtons.OK, r.Exitosa ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    /// <summary>Exporta el resumen de la última migración a TXT o CSV (RF-MIG-13).</summary>
    private void ExportarResumen()
    {
        if (_ultimoResumen is null) return;
        using var dialogo = new SaveFileDialog
        {
            Filter = "Texto (*.txt)|*.txt|CSV separado por punto y coma (*.csv)|*.csv",
            FileName = $"resumen_migracion_{DateTime.Now:yyyyMMdd_HHmm}",
        };
        if (dialogo.ShowDialog(this) != DialogResult.OK) return;
        var r = _ultimoResumen;
        var sb = new StringBuilder();
        if (dialogo.FilterIndex == 2)
        {
            sb.AppendLine("Capa;Archivo;IdBitacora;Leidos;Insertados;Omitidos;Fallidos;Reparados;Advertencias;Estado");
            foreach (var c in r.Capas)
                sb.AppendLine(string.Join(';', c.Definicion.Nombre, c.Archivo, c.IdMigracion, c.Conteos.Leidos, c.Conteos.Insertados,
                    c.Conteos.Omitidos, c.Conteos.Fallidos, c.Conteos.Reparados, c.Conteos.Advertencias, r.Estado));
            sb.AppendLine();
            sb.AppendLine("Capa;Nivel;Codigo;Fila;Campo;Valor;Mensaje");
            foreach (var c in r.Capas)
                foreach (var i in c.Incidencias.Resumen().Concat(c.Incidencias.Detalle))
                    sb.AppendLine(string.Join(';', c.Definicion.Nombre, i.Nivel, i.Codigo, i.Fila, i.Campo, Csv(i.Valor), Csv(i.Mensaje)));
        }
        else sb.Append(_txtRegistro.Text);
        File.WriteAllText(dialogo.FileName, sb.ToString(), new UTF8Encoding(true));
        Registrar($"Resumen exportado a {dialogo.FileName}");
    }

    private async Task ReconstruirIndicesAsync()
    {
        if (_fabrica is null || _operador is not { EsAdministrador: true }) return;
        if (MessageBox.Show("Se recrearán los cuatro índices espaciales con una caja ajustada a la extensión real de los datos. ¿Continuar?",
                "Reconstruir índices espaciales", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!await ValidarOperadorActualAsync(soloAdministrador: true)) return;
        var fabrica = _fabrica!;
        var operador = _operador!;
        _btnIndices.Enabled = false;
        try
        {
            var caja = await new MigracionRepositorio(fabrica).ReconstruirIndicesEspacialesAsync(CancellationToken.None);
            Registrar($"Índices espaciales reconstruidos: {caja}");
            await new BitacoraRepositorio(fabrica).RegistrarAccesoAsync(operador.IdUsuario, operador.Login, "IndicesReconstruidos", true,
                $"migrador@{Environment.MachineName}", caja, CancellationToken.None);
        }
        catch (Exception ex) { Registrar("ERROR al reconstruir índices: " + ex.Message); }
        finally { _btnIndices.Enabled = _fabrica is not null && _operador?.EsAdministrador == true; }
    }

    private void CambiarEstadoControles(bool enEjecucion)
    {
        _btnMigrar.Enabled = !enEjecucion && _fabrica is not null && _operador is not null;
        _btnCancelar.Enabled = enEjecucion;
        _btnExportar.Enabled = !enEjecucion && _ultimoResumen is not null;
        _btnIndices.Enabled = !enEjecucion && _fabrica is not null && _operador?.EsAdministrador == true;
        _btnValidar.Enabled = !enEjecucion;
        _lstCapas.Enabled = _rbAnexar.Enabled = _rbReemplazar.Enabled = !enEjecucion;
        _lblProgreso.ForeColor = SystemColors.ControlText;
    }

    private string _ultimaLinea = "";
    private void Registrar(string texto, bool soloSiCambia = false)
    {
        if (soloSiCambia && texto == _ultimaLinea) return;
        _ultimaLinea = texto;
        _txtRegistro.AppendText($"[{DateTime.Now:HH:mm:ss}] {texto}{Environment.NewLine}");
    }

    // ================================================================= utilidades de interfaz

    private static string Csv(string? s) => s is null ? "" : s.Contains(';') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    private static DataGridView Grilla() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
    };

    private static DataGridViewTextBoxColumn Col(string nombre, string titulo, int ancho) =>
        new() { Name = nombre, HeaderText = titulo, Width = ancho, SortMode = DataGridViewColumnSortMode.NotSortable };

    private static Button Boton(string texto, bool habilitado = true) =>
        new() { Text = texto, AutoSize = true, Padding = new Padding(10, 4, 10, 4), Enabled = habilitado };

    private static TableLayoutPanel Tabla(int columnas)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columnas, AutoSize = true, AutoScroll = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return t;
    }

    private static void Titulo(TableLayoutPanel t, string texto)
    {
        var l = new Label { Text = texto, AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), Margin = new Padding(0, 12, 0, 6) };
        t.Controls.Add(l);
        t.SetColumnSpan(l, 2);
    }

    private static void Fila(TableLayoutPanel t, string etiqueta, Control control)
    {
        t.Controls.Add(new Label { Text = etiqueta, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 6, 0) });
        t.Controls.Add(control);
    }

    private static FlowLayoutPanel Apilar(params Control[] controles) => Apilar(false, controles);

    private static FlowLayoutPanel Apilar(bool horizontal, params Control[] controles)
    {
        var p = new FlowLayoutPanel { AutoSize = true, FlowDirection = horizontal ? FlowDirection.LeftToRight : FlowDirection.TopDown, WrapContents = false };
        p.Controls.AddRange(controles);
        return p;
    }

    private sealed class CapaDetectada(DefinicionCapaMigracion? definicion, ArchivosShapefile archivos)
    {
        public DefinicionCapaMigracion? Definicion { get; } = definicion;
        public ArchivosShapefile Archivos { get; } = archivos;
        public InfoShapefile? Info { get; set; }
        public ResultadoValidacionCapa? Validacion { get; set; }
        public string? EstadoTexto { get; set; }
        public override string ToString() => $"{Definicion?.Nombre} ← {Archivos.Nombre}";
    }
}
