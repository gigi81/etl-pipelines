-- Embedded in the test assembly, not copied beside it.
CREATE TABLE orders (Id INTEGER, Customer TEXT, Amount NUMERIC);
INSERT INTO orders (Id, Customer, Amount) VALUES (1, 'embedded', 10);
