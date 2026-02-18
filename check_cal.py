import json, pyodbc
conn = pyodbc.connect('Driver={ODBC Driver 17 for SQL Server};Server=JOESSERVER2019;Database=DartsMobDB;Uid=DartsMobApp;Pwd=Stewart14s!2;TrustServerCertificate=Yes;')
cur = conn.cursor()
for cam in ['cam0','cam1','cam2']:
    cur.execute('SELECT TOP 1 CalibrationData FROM Calibrations WHERE CameraId=? ORDER BY CreatedAt DESC', cam)
    row = cur.fetchone()
    if row:
        d = json.loads(row[0])
        s20 = d.get('segment_20_index')
        ctr = d.get('center')
        sat = d.get('segment_at_top')
        angles = d.get('segment_angles', [])
        print(f"{cam}: seg20_idx={s20}, center={ctr}, segment_at_top={sat}")
        print(f"  angles[0]={angles[0]:.4f}, angles[1]={angles[1]:.4f}, angles[{s20}]={angles[s20]:.4f} (should be ~where 20 is)")
    else:
        print(f"{cam}: no calibration")
conn.close()
