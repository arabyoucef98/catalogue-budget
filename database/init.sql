CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TABLE IF NOT EXISTS app_users (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    username varchar(80) NOT NULL UNIQUE,
    display_name varchar(160) NOT NULL,
    role varchar(30) NOT NULL CHECK (role IN ('Administrateur', 'Responsable', 'Caissier')),
    password_hash text NOT NULL,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS products (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code varchar(80) NOT NULL UNIQUE,
    designation varchar(240) NOT NULL,
    category varchar(120),
    purchase_price numeric(18,2) NOT NULL DEFAULT 0 CHECK (purchase_price >= 0),
    sale_price numeric(18,2) NOT NULL CHECK (sale_price >= 0),
    stock_quantity numeric(18,3) NOT NULL DEFAULT 0 CHECK (stock_quantity >= 0),
    low_stock_threshold numeric(18,3) NOT NULL DEFAULT 0 CHECK (low_stock_threshold >= 0),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS sales (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    ticket_number bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
    cashier_id uuid NOT NULL REFERENCES app_users(id),
    payment_method varchar(30) NOT NULL,
    subtotal numeric(18,2) NOT NULL CHECK (subtotal >= 0),
    discount numeric(18,2) NOT NULL DEFAULT 0 CHECK (discount >= 0),
    total numeric(18,2) NOT NULL CHECK (total >= 0),
    amount_received numeric(18,2) NOT NULL CHECK (amount_received >= total),
    change_due numeric(18,2) NOT NULL CHECK (change_due >= 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    idempotency_key uuid NOT NULL UNIQUE
);

CREATE TABLE IF NOT EXISTS sale_lines (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    sale_id uuid NOT NULL REFERENCES sales(id) ON DELETE CASCADE,
    product_id uuid NOT NULL REFERENCES products(id),
    code varchar(80) NOT NULL,
    designation varchar(240) NOT NULL,
    quantity numeric(18,3) NOT NULL CHECK (quantity > 0),
    unit_price numeric(18,2) NOT NULL CHECK (unit_price >= 0),
    discount numeric(18,2) NOT NULL DEFAULT 0 CHECK (discount >= 0),
    line_total numeric(18,2) NOT NULL CHECK (line_total >= 0)
);

CREATE TABLE IF NOT EXISTS stock_movements (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    product_id uuid NOT NULL REFERENCES products(id),
    sale_id uuid REFERENCES sales(id),
    movement_type varchar(30) NOT NULL,
    quantity_in numeric(18,3) NOT NULL DEFAULT 0 CHECK (quantity_in >= 0),
    quantity_out numeric(18,3) NOT NULL DEFAULT 0 CHECK (quantity_out >= 0),
    note text,
    created_by uuid NOT NULL REFERENCES app_users(id),
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS audit_events (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    actor_id uuid REFERENCES app_users(id),
    action varchar(80) NOT NULL,
    entity_type varchar(80) NOT NULL,
    entity_id uuid,
    details jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_products_code ON products(code);
CREATE INDEX IF NOT EXISTS ix_sales_created_at ON sales(created_at);
CREATE INDEX IF NOT EXISTS ix_stock_movements_product ON stock_movements(product_id, created_at);
CREATE INDEX IF NOT EXISTS ix_audit_created_at ON audit_events(created_at);
